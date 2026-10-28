using System.Globalization;
using MarumbiSom.Agent;
using MarumbiSom.Agent.Configuration;
using Serilog;

if (args.Contains("--list-devices", StringComparer.OrdinalIgnoreCase))
{
    return await DeviceLister.RunAsync();
}

var simulate = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);
var builder = Host.CreateApplicationBuilder(args.Where(a => !a.StartsWith("--simulate", StringComparison.OrdinalIgnoreCase)).ToArray());

// config.json em C:\ProgramData\MarumbiSom (seção 8.1) sobrepõe o appsettings.json da pasta do programa.
builder.Configuration.AddJsonFile(AgentPaths.ConfigFile, optional: true, reloadOnChange: false);
builder.Services.AddWindowsService(options => options.ServiceName = "MarumbiSom.Agent");

var logDirectory = simulate ? Path.Combine(AppContext.BaseDirectory, "logs") : AgentPaths.LogsDirectory;
builder.Services.AddSerilog((_, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.File(
            Path.Combine(logDirectory, "agent-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 20 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            formatProvider: CultureInfo.InvariantCulture,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    if (simulate || Environment.UserInteractive)
    {
        logger.WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}", formatProvider: CultureInfo.InvariantCulture);
    }
});

builder.Services.AddMarumbiAgent(builder.Configuration, simulate);

var host = builder.Build();
Log.ForContext("SourceContext", "MarumbiSom.Agent").Information("Agente iniciado{Mode}", simulate ? " em modo simulado" : string.Empty);
await host.RunAsync();
return 0;
