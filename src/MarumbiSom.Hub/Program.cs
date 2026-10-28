using System.Globalization;
using MarumbiSom.Hub;
using Serilog;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // appsettings.json fica ao lado do exe; o serviço do Windows começa em C:\Windows\System32.
    ContentRootPath = AppContext.BaseDirectory,
});

var dataDirectory = builder.Configuration[$"{HubOptions.Section}:{nameof(HubOptions.DataDirectory)}"];
var paths = new HubPaths(string.IsNullOrWhiteSpace(dataDirectory) ? HubPaths.DefaultDataDirectory : dataDirectory);
var port = builder.Configuration.GetValue($"{HubOptions.Section}:{nameof(HubOptions.Port)}", 5080);

builder.Host.UseWindowsService(options => options.ServiceName = "MarumbiSom.Hub");
builder.Services.AddSerilog((_, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.File(
            Path.Combine(paths.Logs, "hub-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 20 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            formatProvider: CultureInfo.InvariantCulture,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    if (Environment.UserInteractive)
    {
        logger.WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}", formatProvider: CultureInfo.InvariantCulture);
    }
});
builder.Services.AddMarumbiHub(builder.Configuration, paths);
builder.ListenOnLoopback(port);

var app = builder.Build();
if (args.Contains("--create-admin", StringComparer.OrdinalIgnoreCase))
{
    return await HubCli.CreateAdminAsync(args, app.Services);
}

await HubApp.InitializeDatabaseAsync(app.Services);
app.MapMarumbiHub();
app.Logger.LogInformation("Hub iniciado em http://127.0.0.1:{Port}; dados em {Data}", port, paths.Data);
await app.RunAsync();
return 0;
