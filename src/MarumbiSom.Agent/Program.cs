using System.Globalization;
using MarumbiSom.Agent;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Agent.Tools;
using Serilog;

if (args.Contains("--list-devices", StringComparer.OrdinalIgnoreCase))
{
    return await DeviceLister.RunAsync();
}

if (args.Contains("--set-local-password", StringComparer.OrdinalIgnoreCase))
{
    return AgentCli.SetLocalPassword(args);
}

if (args.Contains("--build-manifest", StringComparer.OrdinalIgnoreCase))
{
    return await AgentCli.BuildManifestAsync(args);
}

var simulate = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);
var hostArgs = args.Where(a => !a.StartsWith("--simulate", StringComparison.OrdinalIgnoreCase)).ToArray();
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = hostArgs,
    // appsettings.json fica ao lado do exe; o serviço do Windows começa em C:\Windows\System32.
    ContentRootPath = AppContext.BaseDirectory,
});

// Pasta de dados: C:\ProgramData\MarumbiSom (seção 8.1), ou Agent:DataDirectory; no modo simulado, ao lado do exe.
var dataDirectory = builder.Configuration["Agent:DataDirectory"];
var paths = !string.IsNullOrWhiteSpace(dataDirectory) ? new AgentDataPaths(dataDirectory)
    : simulate ? new AgentDataPaths(Path.Combine(AppContext.BaseDirectory, "simulacao"))
    : AgentDataPaths.Default;

// config.json da pasta de dados sobrepõe o appsettings.json; a linha de comando sobrepõe os dois.
builder.Configuration.AddJsonFile(paths.ConfigFile, optional: true, reloadOnChange: false);
builder.Configuration.AddCommandLine(hostArgs);
builder.Services.AddWindowsService(options => options.ServiceName = "MarumbiSom.Agent");

builder.Services.AddSerilog((_, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.File(
            Path.Combine(paths.LogsDirectory, "agent-.log"),
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

builder.Services.AddMarumbiAgent(builder.Configuration, paths, simulate);

var host = builder.Build();
Log.ForContext("SourceContext", "MarumbiSom.Agent").Information(
    "Agente iniciado{Mode}; dados em {DataDirectory}", simulate ? " em modo simulado" : string.Empty, paths.Root);
await host.RunAsync();
return 0;
