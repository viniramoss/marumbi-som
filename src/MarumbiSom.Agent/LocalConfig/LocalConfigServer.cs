using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace MarumbiSom.Agent.LocalConfig;

public sealed class LocalConfigOptions
{
    public const string Section = "LocalConfig";

    public int Port { get; set; } = 47800;
}

/// <summary>Página de configuração local em http://127.0.0.1:47800, somente na própria máquina (seção 8.4, regra 5).</summary>
public sealed class LocalConfigServer(
    LocalConfigContext context,
    Microsoft.Extensions.Options.IOptions<LocalConfigOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<LocalConfigServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.Value.Port;
        try
        {
            var app = Build(context, port, loggerFactory);
            logger.LogInformation("Configuração local em http://127.0.0.1:{Port}", port);
            await app.RunAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Sem a página o som continua; só registra (regra 13).
            logger.LogError(ex, "Configuração local indisponível na porta {Port}", port);
        }
    }

    public static WebApplication Build(LocalConfigContext context, int port, ILoggerFactory loggerFactory)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "MarumbiSom.LocalConfig" });
        // Registros do Kestrel vão para o log do agente, só a partir de Warning.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new ForwardingLoggerProvider(loggerFactory));
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        var app = builder.Build();
        app.MapLocalConfig(context);
        return app;
    }

    private sealed class ForwardingLoggerProvider(ILoggerFactory factory) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }
}
