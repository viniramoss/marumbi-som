using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Hub.Panel;
using MarumbiSom.Hub.Updates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarumbiSom.Hub.Tests;

/// <summary>Versões do agente publicadas pelo GitHub Actions e baixadas pelo Hub (D29).</summary>
public sealed class GitHubReleasesTests
{
    private const string Repository = "viniramoss/marumbi-som";
    private const string Token = "github_pat_teste123";

    [Fact]
    public async Task Baixa_a_versao_assinada_mais_nova_e_deixa_como_versao_de_distribuicao()
    {
        var github = new FakeGitHub();
        var (key, _) = ReleaseSigning.NewKeyPair();
        var newest = ReleasePackages.Package("0.6.20", key);
        github.Release("agente-v0.6.19", ("0.6.19", ReleasePackages.Package("0.6.19", key)));
        github.Release("agente-v0.6.20", ("0.6.20", newest));
        github.Release("agente-v0.6.21", ("0.6.21", ReleasePackages.Package("0.6.21", key) with { Signature = null }));
        github.Release("rascunho", ("0.6.30", ReleasePackages.Package("0.6.30", key)), draft: true);
        await using var hub = await StartAsync(github);
        var watcher = hub.App.Services.GetRequiredService<GitHubReleaseWatcher>();
        await watcher.SaveTokenAsync(Token, CancellationToken.None);

        await watcher.CheckAsync(CancellationToken.None);

        var release = await hub.WithDbAsync(db => db.AgentReleases.SingleAsync());
        Assert.Equal(("0.6.20", true, ReleasePackages.Sha(newest.Zip)), (release.Version, release.IsCurrent, release.Sha256));
        Assert.Equal(newest.SignatureText, release.Signature);
        Assert.Equal(newest.Zip, await File.ReadAllBytesAsync(ReleasesEndpoints.PackagePath(hub.Paths, release)));
        Assert.Equal(1, await hub.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == "releases.github")));
        Assert.All(github.Requests, r => Assert.Equal($"Bearer {Token}", r.Authorization));
        Assert.Equal(1, github.Downloads("0.6.20"));

        var admin = await hub.AdminClientAsync();
        var status = await HubHarness.JsonAsync(await admin.GetAsync("/api/releases/github"));
        Assert.Equal((Repository, true, false), (status.GetProperty("repository").GetString(), status.GetProperty("tokenConfigured").GetBoolean(), status.GetProperty("problem").GetBoolean()));
        Assert.Contains("0.6.20", status.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("download").ValueKind);
    }

    [Fact]
    public async Task Nao_baixa_de_novo_nem_traz_de_volta_uma_versao_removida()
    {
        var github = new FakeGitHub();
        var (key, _) = ReleaseSigning.NewKeyPair();
        github.Release("agente-v0.6.20", ("0.6.20", ReleasePackages.Package("0.6.20", key)));
        await using var hub = await StartAsync(github);
        var watcher = hub.App.Services.GetRequiredService<GitHubReleaseWatcher>();
        var admin = await hub.AdminClientAsync();

        await watcher.CheckAsync(CancellationToken.None);
        await watcher.CheckAsync(CancellationToken.None);
        Assert.Equal(1, github.Downloads("0.6.20"));
        Assert.Contains("já está no Hub", (await watcher.StatusAsync(CancellationToken.None)).Message, StringComparison.Ordinal);

        // O administrador volta para uma versão enviada à mão e remove a do GitHub.
        await ReleasePackages.UploadAsync(admin, ReleasePackages.Package("0.6.10", key));
        await admin.PutAsJsonAsync("/api/releases/current", new { version = "0.6.10" });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/releases/0.6.20")).StatusCode);
        await watcher.CheckAsync(CancellationToken.None);
        Assert.Equal(1, github.Downloads("0.6.20"));
        Assert.Contains("removida", (await watcher.StatusAsync(CancellationToken.None)).Message, StringComparison.Ordinal);

        // Uma versão mais nova vem normalmente.
        github.Release("agente-v0.6.21", ("0.6.21", ReleasePackages.Package("0.6.21", key)));
        await watcher.CheckAsync(CancellationToken.None);
        var current = await hub.WithDbAsync(db => db.AgentReleases.SingleAsync(r => r.IsCurrent));
        Assert.Equal("0.6.21", current.Version);
    }

    [Fact]
    public async Task Pacote_que_nao_confere_com_a_assinatura_e_recusado()
    {
        var github = new FakeGitHub();
        var (key, _) = ReleaseSigning.NewKeyPair();
        var good = ReleasePackages.Package("0.6.20", key);
        github.Release("agente-v0.6.20", ("0.6.20", good with { Zip = ReleasePackages.Package("0.6.20", key, extra: "outro").Zip }));
        await using var hub = await StartAsync(github);
        var watcher = hub.App.Services.GetRequiredService<GitHubReleaseWatcher>();

        await watcher.CheckAsync(CancellationToken.None);

        var status = await watcher.StatusAsync(CancellationToken.None);
        Assert.True(status.Problem);
        Assert.Contains("recusada", status.Message, StringComparison.Ordinal);
        Assert.Empty(await hub.WithDbAsync(db => db.AgentReleases.ToListAsync()));
        Assert.Empty(Directory.GetFiles(hub.Paths.Releases));
    }

    [Fact]
    public async Task Baixa_o_pacote_do_hub_mais_novo_e_confere_a_assinatura_do_hub()
    {
        var github = new FakeGitHub();
        var (key, publicKey) = ReleaseSigning.NewKeyPair();
        var keyFile = Path.Combine(Path.GetTempPath(), $"marumbi-chave-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(keyFile, publicKey);
        try
        {
            github.Release("agente-v9.1.0", ("9.1.0", ReleasePackages.Package("9.1.0", key)), hub: ReleasePackages.HubPackage("9.1.0", key));
            // Mais novo, mas assinado como pacote do agente: recusado, e o 9.1.0 continua sendo o disponível.
            github.Release("agente-v9.2.0", ("9.2.0", ReleasePackages.Package("9.2.0", key)), hub: ReleasePackages.HubPackage("9.2.0", key, product: ReleaseProducts.Agent));
            await using var hub = await StartAsync(github, services => services.PostConfigure<HubOptions>(o => o.UpdatePublicKeyFile = keyFile));
            var watcher = hub.App.Services.GetRequiredService<GitHubReleaseWatcher>();
            var updates = hub.App.Services.GetRequiredService<HubUpdates>();

            await watcher.CheckAsync(CancellationToken.None);
            var refused = await updates.StatusAsync(CancellationToken.None);
            Assert.True(refused.Problem);
            Assert.Contains("9.2.0 do Hub no GitHub foi recusada: Assinatura inválida", refused.Message, StringComparison.Ordinal);
            Assert.Null(updates.AvailableVersion);
            Assert.Null(refused.Download);

            github.RemoveHub("9.2.0");
            await watcher.CheckAsync(CancellationToken.None);
            await watcher.CheckAsync(CancellationToken.None);
            var status = await updates.StatusAsync(CancellationToken.None);
            Assert.Equal("9.1.0", status.Available?.Version);
            Assert.Contains("já foi baixada", status.Message, StringComparison.Ordinal);
            Assert.Equal(1, github.Downloads("9.1.0", "Hub"));
            // O agente continua vindo como antes, na mesma procura.
            Assert.Equal("9.2.0", (await hub.WithDbAsync(db => db.AgentReleases.SingleAsync(r => r.IsCurrent))).Version);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public async Task Buscar_atualizacoes_procura_baixa_e_ja_pede_a_troca()
    {
        // D35: um clique procura no GitHub e, se vier versão nova do Hub, pede a troca sem outra confirmação.
        var github = new FakeGitHub();
        var (key, publicKey) = ReleaseSigning.NewKeyPair();
        var keyFile = Path.Combine(Path.GetTempPath(), $"marumbi-chave-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(keyFile, publicKey);
        try
        {
            await using var hub = await StartAsync(github, services => services.PostConfigure<HubOptions>(o => o.UpdatePublicKeyFile = keyFile));
            var admin = await hub.AdminClientAsync();
            var watcher = hub.App.Services.GetRequiredService<GitHubReleaseWatcher>();
            var updates = hub.App.Services.GetRequiredService<HubUpdates>();

            var withoutUpdater = await admin.PostAsync("/api/hub/update/check", null);
            Assert.Equal(HttpStatusCode.BadRequest, withoutUpdater.StatusCode);
            Assert.Contains("atualizador ainda não está instalado", await withoutUpdater.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(updates.Files.UpdaterExe)!);
            await File.WriteAllTextAsync(updates.Files.UpdaterExe, "");

            // Nada publicado ainda: o cartão diz e nada é pedido.
            // A procura do botão roda em segundo plano; aqui ela é esperada rodando uma em seguida.
            Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/hub/update/check", null)).StatusCode);
            await watcher.CheckAsync(CancellationToken.None);
            var nothing = await updates.StatusAsync(CancellationToken.None);
            Assert.False(nothing.Searching);
            Assert.Null(nothing.Pending);
            Assert.Contains("Nenhum pacote do Hub", nothing.Message, StringComparison.Ordinal);

            github.Release("agente-v9.1.0", ("9.1.0", ReleasePackages.Package("9.1.0", key)), hub: ReleasePackages.HubPackage("9.1.0", key));
            Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/hub/update/check", null)).StatusCode);
            await watcher.CheckAsync(CancellationToken.None);
            var status = await updates.StatusAsync(CancellationToken.None);
            Assert.False(status.Searching);
            Assert.Equal(("9.1.0", "waiting", false), (status.Pending?.Version, status.Pending?.Stage, status.Pending?.Automatic));
            Assert.True(await hub.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "hub.update.search")));
            Assert.True(await hub.WithDbAsync(db => db.AuditLog.AnyAsync(a => a.Action == "hub.update.request" && a.Target == "9.1.0")));

            // Com a troca pedida, o botão não procura de novo.
            var again = await admin.PostAsync("/api/hub/update/check", null);
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
            Assert.Contains("Já há", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(keyFile);
        }
    }

    [Fact]
    public async Task Explica_quando_o_github_recusa()
    {
        var github = new FakeGitHub { ListStatus = HttpStatusCode.NotFound };
        await using var hub = await StartAsync(github);
        var watcher = hub.App.Services.GetRequiredService<GitHubReleaseWatcher>();

        await watcher.CheckAsync(CancellationToken.None);
        var missing = await watcher.StatusAsync(CancellationToken.None);
        Assert.True(missing.Problem);
        Assert.Contains("cadastre o token", missing.Message, StringComparison.Ordinal);
        Assert.Null(github.Requests.Single().Authorization);

        github.ListStatus = HttpStatusCode.Unauthorized;
        await watcher.SaveTokenAsync(Token, CancellationToken.None);
        await watcher.CheckAsync(CancellationToken.None);
        Assert.Contains("recusou o token", (await watcher.StatusAsync(CancellationToken.None)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_fica_cifrado_e_nunca_volta_para_o_painel()
    {
        await using var hub = await StartAsync(new FakeGitHub());
        var admin = await hub.AdminClientAsync();

        var saved = await admin.PutAsJsonAsync("/api/releases/github/token", new { token = $"  {Token} " });
        var text = await saved.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("tokenConfigured").GetBoolean());

        var stored = await hub.WithDbAsync(db => db.Settings.SingleAsync(s => s.Key == GitHubReleaseWatcher.TokenKey));
        Assert.DoesNotContain(Token, stored.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, await admin.GetStringAsync("/api/releases/github"), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/releases/github/token", new { token = "tem espaço no meio" })).StatusCode);

        var cleared = await HubHarness.JsonAsync(await admin.PutAsJsonAsync("/api/releases/github/token", new { token = (string?)null }));
        Assert.False(cleared.GetProperty("tokenConfigured").GetBoolean());
        Assert.False(await hub.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == GitHubReleaseWatcher.TokenKey)));
        Assert.Equal(2, await hub.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == "releases.github.token")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await hub.AnonymousClient().GetAsync("/api/releases/github")).StatusCode);
    }

    [Fact]
    public async Task Procura_desligada_sem_repositorio()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/releases/github/check", null)).StatusCode);
        var status = await HubHarness.JsonAsync(await admin.GetAsync("/api/releases/github"));
        Assert.Equal(JsonValueKind.Null, status.GetProperty("repository").ValueKind);
    }

    private static Task<HubHarness> StartAsync(FakeGitHub github, Action<IServiceCollection>? configure = null) =>
        HubHarness.StartAsync(gitHubRepository: Repository, configureServices: services =>
        {
            services.AddHttpClient(GitHubReleaseWatcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => github);
            configure?.Invoke(services);
        });

    /// <summary>API de Releases do GitHub em memória: lista e download dos arquivos pelo endereço da API.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        private readonly List<object> _releases = [];
        private readonly Dictionary<string, byte[]> _assets = [];
        private readonly Dictionary<string, string> _names = [];
        private readonly ConcurrentDictionary<string, int> _downloads = [];
        private int _nextId = 1;

        public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;

        public ConcurrentQueue<(string Path, string? Authorization)> Requests { get; } = [];

        public int Downloads(string version, string product = "Agent") => _downloads.GetValueOrDefault($"MarumbiSom.{product}-{version}.zip");

        /// <param name="hub">Pacote do Hub da mesma Release (D32 a).</param>
        public void Release(string tag, (string Version, ReleasePackages.ReleaseFiles Files) package, bool draft = false, ReleasePackages.ReleaseFiles? hub = null)
        {
            var assets = new List<object> { Asset($"MarumbiSom.Agent-{package.Version}.zip", package.Files.Zip) };
            if (package.Files.Signature is { } signature)
            {
                assets.Add(Asset($"MarumbiSom.Agent-{package.Version}.sig.json", signature));
            }

            assets.Add(Asset($"MarumbiSom.Agent-Setup-{package.Version}.exe", [1, 2, 3]));
            if (hub is not null)
            {
                assets.Add(Asset($"MarumbiSom.Hub-{package.Version}.zip", hub.Zip));
                assets.Add(Asset($"MarumbiSom.Hub-{package.Version}.sig.json", hub.Signature!));
            }

            lock (_releases)
            {
                _releases.Insert(0, new { tag_name = tag, draft, prerelease = false, assets });
            }
        }

        /// <summary>Tira da lista o pacote do Hub de uma versão (Release refeita sem ele).</summary>
        public void RemoveHub(string version)
        {
            lock (_releases)
            {
                foreach (var path in _names.Where(n => n.Value.StartsWith($"MarumbiSom.Hub-{version}.", StringComparison.Ordinal)).Select(n => n.Key).ToList())
                {
                    _assets.Remove(path);
                    _names.Remove(path);
                }

                var live = _names.Keys.ToHashSet(StringComparer.Ordinal);
                for (var i = 0; i < _releases.Count; i++)
                {
                    var release = JsonSerializer.SerializeToElement(_releases[i]);
                    var assets = release.GetProperty("assets").EnumerateArray()
                        .Where(a => live.Contains(new Uri(a.GetProperty("url").GetString()!).AbsolutePath))
                        .Select(a => JsonSerializer.Deserialize<object>(a.GetRawText())!)
                        .ToList();
                    _releases[i] = new
                    {
                        tag_name = release.GetProperty("tag_name").GetString(),
                        draft = release.GetProperty("draft").GetBoolean(),
                        prerelease = false,
                        assets,
                    };
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Enqueue((path, request.Headers.Authorization?.ToString()));
            if (path == $"/repos/{Repository}/releases?per_page=20")
            {
                if (ListStatus != HttpStatusCode.OK)
                {
                    return Task.FromResult(new HttpResponseMessage(ListStatus));
                }

                lock (_releases)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(_releases.ToList()) });
                }
            }

            lock (_releases)
            {
                if (_assets.TryGetValue(path, out var data))
                {
                    Assert.Equal("application/octet-stream", request.Headers.Accept.Single().MediaType);
                    _downloads.AddOrUpdate(_names[path], 1, (_, count) => count + 1);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private object Asset(string name, byte[] data)
        {
            var id = _nextId++;
            var path = $"/repos/{Repository}/releases/assets/{id}";
            _assets[path] = data;
            _names[path] = name;
            return new { id, name, size = data.LongLength, url = $"https://api.github.com{path}" };
        }
    }
}
