using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Panel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Hub.Tests;

/// <summary>Hub completo em memória (TestServer), com pasta de dados temporária e relógio controlado.</summary>
public sealed class HubHarness : IAsyncDisposable
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "senha-muito-forte";

    private HubHarness(WebApplication app, string root, FakeTimeProvider time)
    {
        App = app;
        Root = root;
        Time = time;
        Server = app.GetTestServer();
    }

    public WebApplication App { get; }

    public TestServer Server { get; }

    public string Root { get; }

    public FakeTimeProvider Time { get; }

    public HubPaths Paths => App.Services.GetRequiredService<HubPaths>();

    /// <param name="webRoot">Pasta com o build do painel (padrão: nenhuma).</param>
    /// <param name="gitHubRepository">Repositório das versões do agente no GitHub (padrão: procura desligada).</param>
    /// <param name="configureServices">Troca serviços depois do Hub montado (ex.: o HTTP do GitHub).</param>
    public static async Task<HubHarness> StartAsync(
        DateTimeOffset? now = null, string? webRoot = null, string? gitHubRepository = null, Action<IServiceCollection>? configureServices = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "marumbi-hub-tests", Guid.NewGuid().ToString("N"));
        var time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 10, 23, 13, 0, 0, TimeSpan.FromHours(-3)));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing", WebRootPath = webRoot });
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddMarumbiHub(builder.Configuration, new HubPaths(Path.Combine(root, "data")));
        builder.Services.PostConfigure<HubOptions>(o => o.GitHubRepository = gitHubRepository);
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        await HubApp.InitializeDatabaseAsync(app.Services);
        app.MapMarumbiHub();
        await app.StartAsync();

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDb>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
            var user = new User { Username = AdminUser, PasswordHash = string.Empty, CreatedAt = time.GetUtcNow() };
            user.PasswordHash = hasher.HashPassword(user, AdminPassword);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        return new HubHarness(app, root, time);
    }

    /// <summary>Cliente sem login, com o cabeçalho anti-CSRF.</summary>
    public HttpClient AnonymousClient(bool csrf = true)
    {
        var client = Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost/");
        if (csrf)
        {
            client.DefaultRequestHeaders.Add(PanelAuth.CsrfHeader, "1");
        }

        return client;
    }

    /// <summary>Cliente logado no painel (o cookie é Secure, então o cabeçalho é copiado à mão).</summary>
    public async Task<HttpClient> AdminClientAsync()
    {
        var client = AnonymousClient();
        client.DefaultRequestHeaders.Add("Cookie", await AdminCookieAsync(client));
        return client;
    }

    /// <summary>Cookie de sessão do painel no formato do cabeçalho <c>Cookie</c>.</summary>
    public async Task<string> AdminCookieAsync(HttpClient? client = null, string password = AdminPassword)
    {
        client ??= AnonymousClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = AdminUser, password });
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(PanelAuth.CookieName, StringComparison.Ordinal));
        return cookie.Split(';')[0];
    }

    public HttpClient AgentClient(string token)
    {
        var client = Server.CreateClient();
        client.BaseAddress = new Uri("https://localhost/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<HubDb, Task<T>> action)
    {
        await using var scope = App.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<HubDb>());
    }

    public Task WithDbAsync(Func<HubDb, Task> action) => WithDbAsync(async db =>
    {
        await action(db);
        return true;
    });

    /// <summary>Faixa já processada na biblioteca, sem passar pelo ffmpeg.</summary>
    public async Task<string> SeedTrackAsync(string title, long durationMs = 200_000, TrackKind kind = TrackKind.Music, string? artist = null)
    {
        var content = System.Text.Encoding.UTF8.GetBytes($"áudio falso de {title} {Guid.NewGuid()}");
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        var path = Paths.LibraryFile(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
        await WithDbAsync(db =>
        {
            db.Tracks.Add(new Data.Track
            {
                Hash = hash,
                Kind = kind,
                Title = title,
                Artist = artist,
                DurationMs = durationMs,
                SizeBytes = content.Length,
                OriginalName = title + ".mp3",
                CreatedAt = Time.GetUtcNow(),
            });
            return db.SaveChangesAsync();
        });
        return hash;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode} {response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
