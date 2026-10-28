using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Reflection;
using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Playback;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Agent.Sync;
using MarumbiSom.Agent.Windows.Abstractions;

namespace MarumbiSom.Agent.LocalConfig;

/// <summary>Troca da caixa em uso; implementado pelo <see cref="BluetoothGuardian"/>.</summary>
public interface ISpeakerSelection
{
    BluetoothAddress? SpeakerAddress { get; }

    void SetSpeaker(BluetoothAddress? address);
}

/// <summary>Vinculação e sincronização com o Hub (Etapa 3).</summary>
public sealed record HubContext(HubLink Link, SyncEngine Sync, HubClient Live);

/// <summary>Dependências da página de configuração local.</summary>
public sealed record LocalConfigContext(
    LocalAuth Auth,
    LocalConfigFile Config,
    PlayerEngine Player,
    IBluetoothConnection Bluetooth,
    ISpeakerSelection Speaker,
    IBluetoothSpeakerControl Speakers,
    ManifestStore Manifests,
    string LogsDirectory,
    TimeProvider Time,
    HubContext? Hub = null);

/// <summary>
/// Rotas da configuração local (seção 8.4). Só atende 127.0.0.1/localhost, exige o cabeçalho
/// <c>X-MarumbiSom</c> em todo POST (bloqueia formulários e scripts de outros sites) e cookie de sessão
/// <c>SameSite=Strict</c>.
/// </summary>
public static class LocalConfigEndpoints
{
    public const string SessionCookie = "marumbisom_sessao";
    public const string CsrfHeader = "X-MarumbiSom";
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;

    public static void MapLocalConfig(this WebApplication app, LocalConfigContext context)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(context);
        app.Use(async (http, next) =>
        {
            if (Reject(http) is { } status)
            {
                http.Response.StatusCode = status;
                return;
            }

            var headers = http.Response.Headers;
            headers.XFrameOptions = "DENY";
            headers.XContentTypeOptions = "nosniff";
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = "default-src 'self'; frame-ancestors 'none'; form-action 'self'";
            await next(http);
        });

        app.MapGet("/", () => Asset("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => Asset("app.js", "text/javascript; charset=utf-8"));
        app.MapGet("/app.css", () => Asset("app.css", "text/css; charset=utf-8"));

        app.MapGet("/api/session", (HttpContext http) => Results.Ok(new
        {
            authenticated = context.Auth.Validate(http.Request.Cookies[SessionCookie]),
            passwordConfigured = context.Auth.PasswordConfigured,
            lockedUntil = context.Auth.LockedUntil,
        }));

        app.MapPost("/api/login", (HttpContext http, LoginRequest request) =>
        {
            var result = context.Auth.Login(request.Password);
            switch (result.Outcome)
            {
                case LoginOutcome.Success:
                    http.Response.Cookies.Append(SessionCookie, result.SessionToken!, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Strict,
                        Path = "/",
                        IsEssential = true,
                    });
                    return Results.Ok(new { ok = true });
                case LoginOutcome.NotConfigured:
                    return Error(StatusCodes.Status409Conflict, "Senha ainda não definida. Um administrador precisa rodar: MarumbiSom.Agent.exe --set-local-password");
                case LoginOutcome.LockedOut:
                    return Error(StatusCodes.Status423Locked, $"Bloqueado por excesso de tentativas até {Local(result.LockedUntil!.Value):HH:mm}.");
                default:
                    return Error(StatusCodes.Status401Unauthorized, $"Senha incorreta. Tentativas restantes: {result.RemainingAttempts}.");
            }
        });

        var api = app.MapGroup("/api").AddEndpointFilter(async (filter, next) =>
            context.Auth.Validate(filter.HttpContext.Request.Cookies[SessionCookie])
                ? await next(filter)
                : Error(StatusCodes.Status401Unauthorized, "Sessão expirada. Entre de novo."));

        api.MapPost("/logout", (HttpContext http) =>
        {
            context.Auth.Logout(http.Request.Cookies[SessionCookie]);
            http.Response.Cookies.Delete(SessionCookie);
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/status", () => Results.Ok(Status(context)));

        api.MapGet("/devices", async (CancellationToken cancellationToken) =>
        {
            var selected = context.Speaker.SpeakerAddress;
            var devices = await context.Speakers.ListPairedDevicesAsync(cancellationToken);
            return Results.Ok(devices.Select(d => new
            {
                address = d.Address.ToString(),
                name = d.Name,
                connected = d.IsConnected,
                selected = selected is { } s && s.Equals(d.Address),
            }));
        });

        api.MapPost("/speaker", async (SpeakerRequest request, CancellationToken cancellationToken) =>
        {
            if (!BluetoothAddress.TryParse(request.Address, out var address))
            {
                return Error(StatusCodes.Status400BadRequest, "Endereço de caixa inválido.");
            }

            var paired = await context.Speakers.ListPairedDevicesAsync(cancellationToken);
            if (!paired.Any(d => d.Address.Equals(address)))
            {
                return Error(StatusCodes.Status400BadRequest, "Essa caixa não está pareada no Windows.");
            }

            context.Config.Set("Bluetooth", "SpeakerAddress", address.ToString());
            context.Speaker.SetSpeaker(address);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/test-sound", async (CancellationToken cancellationToken) =>
        {
            var result = await context.Player.ExecuteAsync(new PlayerCommand(PlayerCommandKind.TestSound), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result.Ok ? Results.Ok(new { ok = true }) : Error(StatusCodes.Status409Conflict, result.Error ?? "Falha ao tocar o som de teste.");
        });

        api.MapPost("/hub", async (HubLinkRequest request, CancellationToken cancellationToken) =>
        {
            if (context.Hub is null)
            {
                return Error(StatusCodes.Status404NotFound, "Sincronização com o Hub indisponível.");
            }

            try
            {
                var info = await context.Hub.Link.EnrollAsync(request.Url ?? string.Empty, request.Code ?? string.Empty, cancellationToken);
                return Results.Ok(new { ok = true, storeId = info.StoreId, storeName = info.StoreName });
            }
            catch (HubEnrollException ex)
            {
                return Error(StatusCodes.Status400BadRequest, ex.Message);
            }
        });

        api.MapPost("/hub/unlink", () =>
        {
            context.Hub?.Link.Unlink();
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/logs", (int? lines) => Results.Text(TailLog(context.LogsDirectory, Math.Clamp(lines ?? 200, 10, 2000)), "text/plain; charset=utf-8"));

        api.MapPost("/password", (HttpContext http, PasswordRequest request) =>
        {
            var check = context.Auth.CheckPassword(request.Current);
            if (check.Outcome == LoginOutcome.LockedOut)
            {
                return Error(StatusCodes.Status423Locked, "Bloqueado por excesso de tentativas. Tente mais tarde.");
            }

            if (check.Outcome != LoginOutcome.Success)
            {
                return Error(StatusCodes.Status400BadRequest, "Senha atual incorreta.");
            }

            if (LocalPassword.Validate(request.New) is { } problem)
            {
                return Error(StatusCodes.Status400BadRequest, problem);
            }

            context.Auth.SetPassword(request.New!);
            http.Response.Cookies.Delete(SessionCookie);
            return Results.Ok(new { ok = true });
        });

        DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local);
    }

    private static int? Reject(HttpContext http)
    {
        // Regra 5: nada de fora da máquina. Kestrel já escuta só em 127.0.0.1; isto é defesa em profundidade.
        if (http.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip))
        {
            return StatusCodes.Status403Forbidden;
        }

        // Host diferente de 127.0.0.1/localhost indica DNS rebinding.
        if (http.Request.Host.Host is not ("127.0.0.1" or "localhost"))
        {
            return StatusCodes.Status400BadRequest;
        }

        if (!HttpMethods.IsGet(http.Request.Method))
        {
            if (http.Request.Headers[CsrfHeader] != "1")
            {
                return StatusCodes.Status403Forbidden;
            }

            var origin = http.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !(Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Host is "127.0.0.1" or "localhost"))
            {
                return StatusCodes.Status403Forbidden;
            }
        }

        return null;
    }

    private static object Status(LocalConfigContext context)
    {
        var player = context.Player.Snapshot;
        var manifest = context.Manifests.Active;
        var now = context.Time.GetUtcNow();
        var playlistId = player.PlaylistId ?? player.WindowPlaylistId;
        return new
        {
            agentVersion = typeof(LocalConfigEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            uptimeS = (long)(now - StartedAt).TotalSeconds,
            storeId = manifest?.StoreId,
            manifestVersion = manifest?.Version,
            hub = HubStatus(context),
            bluetooth = context.Bluetooth.Status,
            speakerAddress = context.Speaker.SpeakerAddress?.ToString(),
            player = new
            {
                status = player.Status,
                reason = player.Reason,
                title = player.Title,
                artist = player.Artist,
                playlist = playlistId,
                playlistName = playlistId is not null && manifest?.Playlists.TryGetValue(playlistId, out var definition) == true ? definition.Name : null,
                positionS = player.PositionS,
                durationS = player.DurationS,
                volume = player.Volume,
                crossfading = player.Crossfading,
                pausedByAdmin = player.PausedByAdmin,
                windowEnd = player.WindowEnd,
                spot = player.Spot,
                upcoming = player.Upcoming,
            },
        };
    }

    private static object? HubStatus(LocalConfigContext context)
    {
        if (context.Hub is not { } hub)
        {
            return null;
        }

        var link = hub.Link.Current;
        var pending = context.Manifests.Pending;
        return new
        {
            linked = link is not null,
            url = link?.Url.ToString(),
            storeId = link?.StoreId,
            storeName = link?.StoreName,
            connected = hub.Live.Connected,
            connectedSince = hub.Live.ConnectedSince,
            error = link is null ? null : hub.Sync.LastError ?? hub.Live.LastError,
            lastCheckAt = hub.Sync.LastCheckAt,
            pendingVersion = link is not null && pending?.StoreId == link.StoreId ? pending.Version : (long?)null,
            progress = hub.Sync.Progress,
        };
    }

    private static string TailLog(string directory, int lines)
    {
        if (!Directory.Exists(directory))
        {
            return "(sem logs)";
        }

        var latest = new DirectoryInfo(directory).EnumerateFiles("agent-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (latest is null)
        {
            return "(sem logs)";
        }

        // O Serilog mantém o arquivo aberto; FileShare.ReadWrite permite ler mesmo assim.
        using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int maxBytes = 512 * 1024;
        if (stream.Length > maxBytes)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream);
        var all = reader.ReadToEnd().TrimEnd('\r', '\n').Split('\n');
        return string.Join('\n', all.TakeLast(lines));
    }

    private static IResult Asset(string name, string contentType)
    {
        using var stream = typeof(LocalConfigEndpoints).Assembly.GetManifestResourceStream("LocalConfig." + name);
        if (stream is null)
        {
            return Results.NotFound();
        }

        using var reader = new StreamReader(stream);
        return Results.Text(reader.ReadToEnd(), contentType);
    }

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    public sealed record LoginRequest(string? Password);

    public sealed record SpeakerRequest(string? Address);

    public sealed record PasswordRequest(string? Current, string? New);

    public sealed record HubLinkRequest(string? Url, string? Code);
}
