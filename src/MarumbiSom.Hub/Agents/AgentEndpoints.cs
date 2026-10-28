using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MarumbiSom.Hub.Agents;

/// <summary>API do agente (seção 11.3), autenticada pelo token Bearer da loja.</summary>
public static class AgentEndpoints
{
    public const string EnrollRateLimit = "enroll";
    public static readonly TimeSpan EnrollCodeLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Sem 0/O, 1/I/L: fácil de ditar e digitar.</summary>
    private const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const string StoreItem = "marumbisom.store";

    // Pela contentHash (e não loja+versão): um banco restaurado de backup pode repetir números de versão com outro conteúdo.
    private static readonly ConcurrentDictionary<string, HashSet<string>> AllowedFiles = new(StringComparer.Ordinal);

    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string HashSecret(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>Código de 8 caracteres (seção 8.5), mostrado como XXXX-XXXX.</summary>
    public static string NewEnrollCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        }

        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
    }

    public static string NormalizeCode(string code) =>
        new((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static void MapAgentApi(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup("/agent");
        agent.MapPost("/enroll", EnrollAsync).RequireRateLimiting(EnrollRateLimit);

        var authenticated = agent.MapGroup(string.Empty).AddEndpointFilter(async (context, next) =>
        {
            var store = await AuthenticateAsync(context.HttpContext);
            if (store is null)
            {
                return Results.Json(new { error = "Token inválido." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            context.HttpContext.Items[StoreItem] = store;
            return await next(context);
        });
        authenticated.MapGet("/manifest", ManifestAsync);
        authenticated.MapGet("/files/{hash}", FileAsync);
        authenticated.MapGet("/ws", SocketAsync);
    }

    private static async Task<IResult> EnrollAsync(EnrollRequest request, HubDb db, TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger(typeof(AgentEndpoints).FullName!);
        var codeHash = HashSecret(NormalizeCode(request.Code));
        var now = time.GetUtcNow();
        var code = await db.EnrollCodes.SingleOrDefaultAsync(c => c.CodeHash == codeHash, cancellationToken);
        if (code is null || code.UsedAt is not null || code.ExpiresAt <= now)
        {
            logger.LogWarning("Vinculação recusada: código {Reason}", code is null ? "inexistente" : code.UsedAt is not null ? "já usado" : "vencido");
            return Results.Json(new { error = "Código de vinculação inválido ou vencido." }, statusCode: StatusCodes.Status400BadRequest);
        }

        var store = await db.Stores.SingleAsync(s => s.Id == code.StoreId, cancellationToken);
        var token = NewToken();
        store.TokenHash = HashSecret(token);
        code.UsedAt = now;
        db.Events.Add(new StoreEvent { StoreId = store.Id, Type = "agent.enrolled", CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Loja {Store} vinculada", store.Slug);
        return Results.Ok(new EnrollResponse(store.Slug, store.Name, token));
    }

    private static async Task<IResult> ManifestAsync(HttpContext context, HubDb db, CancellationToken cancellationToken)
    {
        var store = (Store)context.Items[StoreItem]!;
        var manifest = await ManifestPublisher.LatestAsync(db, store.Id, cancellationToken);
        if (manifest is null)
        {
            return Results.NotFound(new { error = "A loja ainda não tem manifesto." });
        }

        var etag = new EntityTagHeaderValue($"\"{manifest.Version}\"");
        if (context.Request.Headers.IfNoneMatch.Any(v => v == etag.Tag.Value))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        context.Response.Headers.ETag = etag.Tag.Value;
        return Results.Text(manifest.Json, "application/json", Encoding.UTF8);
    }

    private static async Task<IResult> FileAsync(string hash, HttpContext context, HubDb db, HubPaths paths, CancellationToken cancellationToken)
    {
        var store = (Store)context.Items[StoreItem]!;
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            return Results.BadRequest(new { error = "Hash inválido." });
        }

        // A loja só baixa arquivos do próprio manifesto.
        var manifest = await ManifestPublisher.LatestAsync(db, store.Id, cancellationToken);
        if (manifest is null)
        {
            return Results.NotFound();
        }

        if (AllowedFiles.Count > 500)
        {
            AllowedFiles.Clear();
        }

        var allowed = AllowedFiles.GetOrAdd(manifest.ContentHash, _ => [.. ManifestPublisher.Deserialize(manifest).Tracks.Keys]);
        var path = paths.LibraryFile(hash);
        if (!allowed.Contains(hash) || !File.Exists(path))
        {
            return Results.NotFound(new { error = "Arquivo fora do manifesto da loja." });
        }

        return Results.File(path, "audio/mp4", enableRangeProcessing: true);
    }

    private static async Task SocketAsync(HttpContext context, AgentConnections connections, IHostApplicationLifetime lifetime)
    {
        var store = (Store)context.Items[StoreItem]!;
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        // Ao parar o serviço, a conexão fecha na hora; sem isso o Kestrel espera 30 s pelo fim da conexão aberta.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        await connections.RunAsync(store.Id, store.Slug, socket, stopping.Token);
    }

    private static async Task<Store?> AuthenticateAsync(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || header.Length < 20)
        {
            return null;
        }

        var tokenHash = HashSecret(header["Bearer ".Length..].Trim());
        var db = context.RequestServices.GetRequiredService<HubDb>();
        return await db.Stores.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == tokenHash, context.RequestAborted);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public sealed record EnrollRequest(string Code);

    public sealed record EnrollResponse(string StoreId, string StoreName, string Token);
}
