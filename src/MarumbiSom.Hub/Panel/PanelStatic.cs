using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace MarumbiSom.Hub.Panel;

/// <summary>
/// Serve o build do painel (<c>wwwroot</c>, D23 f) e o WebSocket <c>/ws/panel</c>. Rotas do painel (ex.: /lojas/3)
/// voltam para o index.html; <c>/api</c>, <c>/agent</c> e <c>/ws</c> nunca.
/// </summary>
public static class PanelStatic
{
    /// <summary>Só a própria origem: o painel não carrega nada de fora.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; connect-src 'self'; font-src 'self'; "
        + "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static readonly string[] ServerPrefixes = ["/api", "/agent", "/ws", "/openapi", "/healthz"];

    public static void MapPanel(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var files = app.Environment.WebRootFileProvider;
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = context =>
            {
                // Arquivos de /assets têm o hash no nome: podem ficar no cache para sempre; o resto é revalidado.
                context.Context.Response.Headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            },
        });

        app.MapGet("/ws/panel", SocketAsync).RequireAuthorization(PanelAuth.Policy).ExcludeFromDescription();
        app.MapFallback(context => IndexAsync(context, files)).ExcludeFromDescription();
    }

    /// <summary>
    /// Aceita só conexões vindas do próprio painel: a origem precisa ser o mesmo endereço (o Tailscale Serve repassa o
    /// Host original e o X-Forwarded-Host).
    /// </summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return string.Equals(host, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, request.Headers["X-Forwarded-Host"].ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SocketAsync(HttpContext context, PanelHub panel)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!IsSameOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        await panel.RunAsync(socket, context.RequestAborted);
    }

    private static async Task IndexAsync(HttpContext context, IFileProvider files)
    {
        var path = context.Request.Path;
        var index = files.GetFileInfo("index.html");
        if (!HttpMethods.IsGet(context.Request.Method) || ServerPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))
            || Path.HasExtension(path.Value) || !index.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            if (!index.Exists && !path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                await context.Response.WriteAsync("Painel não instalado nesta pasta (falta wwwroot\\index.html).");
            }

            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers[HeaderNames.ContentLength] = index.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.SendFileAsync(index);
    }
}
