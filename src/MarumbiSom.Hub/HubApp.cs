using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MarumbiSom.Hub.Agents;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Library;
using MarumbiSom.Hub.Manifests;
using MarumbiSom.Hub.Panel;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Hub;

/// <summary>Monta o Hub (serviços e rotas); usado pelo Program e pelos testes.</summary>
public static class HubApp
{
    public static IServiceCollection AddMarumbiHub(this IServiceCollection services, IConfiguration configuration, HubPaths paths)
    {
        services.Configure<HubOptions>(configuration.GetSection(HubOptions.Section));
        services.AddSingleton(paths);
        services.TryAddSingletonTimeProvider();
        services.AddDbContext<HubDb>(options => options.UseSqlite(ConnectionString(paths)));
        services.ConfigureHttpJsonOptions(options => HubJson.Configure(options.SerializerOptions));
        services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = TracksEndpoints.MaxRequestBytes);
        services.AddPanelAuth();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Seção 12.1: no máximo 5 tentativas de login por minuto.
            options.AddFixedWindowLimiter(PanelAuth.LoginRateLimit, o =>
            {
                o.PermitLimit = 5;
                o.Window = TimeSpan.FromMinutes(1);
                o.QueueLimit = 0;
            });
            options.AddFixedWindowLimiter(AgentEndpoints.EnrollRateLimit, o =>
            {
                o.PermitLimit = 10;
                o.Window = TimeSpan.FromMinutes(1);
                o.QueueLimit = 0;
            });
        });
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<HubOptions>>().Value;
            return new Ffmpeg(
                HubOptions.ResolveTool(options.FfmpegPath, "ffmpeg"),
                HubOptions.ResolveTool(options.FfprobePath, "ffprobe"));
        });
        services.AddSingleton<AudioProcessor>();
        services.AddSingleton<AgentConnections>();
        services.AddSingleton<ManifestPublisher>();
        services.AddSingleton<IngestionWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<IngestionWorker>());
        services.AddSingleton<DailyMaintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<DailyMaintenance>());
        return services;
    }

    public static string ConnectionString(HubPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new SqliteConnectionStringBuilder { DataSource = paths.Database, ForeignKeys = true, DefaultTimeout = 30, Pooling = true }.ToString();
    }

    /// <summary>Cria as pastas, aplica as migrações e liga o modo WAL (seção 4).</summary>
    public static async Task InitializeDatabaseAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.GetRequiredService<HubPaths>().EnsureCreated();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }

    public static WebApplication MapMarumbiHub(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            await next();
        });
        app.UseWebSockets();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseCsrfHeader();

        app.MapPanelAuth();
        var api = app.MapGroup("/api").RequireAuthorization(PanelAuth.Policy);
        api.MapTracks();
        api.MapPlaylists();
        api.MapStores();
        app.MapAgentApi();
        app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
        return app;
    }

    /// <summary>Kestrel somente em 127.0.0.1 (seção 6.1).</summary>
    public static void ListenOnLoopback(this WebApplicationBuilder builder, int port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, port);
        });
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
