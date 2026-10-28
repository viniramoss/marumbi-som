using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Updates;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarumbiSom.Hub.Tests;

/// <summary>Atualização automática do Hub, do lado do Hub (D32 b, e, f): pacote, pedido, resultado, madrugada e banco.</summary>
public sealed class HubUpdatesTests : IDisposable
{
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "marumbi-hub-update-keys", Guid.NewGuid().ToString("N"));
    private readonly (string PrivateKeyFile, string PublicKey) _key = ReleaseSigning.NewKeyPair();

    public HubUpdatesTests()
    {
        Directory.CreateDirectory(_keyDirectory);
        File.WriteAllText(KeyFile, $"# chave de teste\n{_key.PublicKey}\n");
    }

    private string KeyFile => Path.Combine(_keyDirectory, ReleaseSigning.PublicKeyFileName);

    [Fact]
    public async Task Pacote_conferido_fica_disponivel_e_o_pedido_vai_para_o_atualizador()
    {
        await using var hub = await StartAsync();
        var admin = await hub.AdminClientAsync();
        var files = Updates(hub).Files;

        var initial = await HubHarness.JsonAsync(await admin.GetAsync("/api/hub/update"));
        Assert.Equal(HubUpdates.CurrentVersion, initial.GetProperty("currentVersion").GetString());
        Assert.True(initial.GetProperty("supported").GetBoolean());
        Assert.False(initial.GetProperty("updaterInstalled").GetBoolean());
        Assert.True(initial.GetProperty("autoUpdate").GetBoolean());
        Assert.Equal("03:00", initial.GetProperty("autoUpdateTime").GetString());
        Assert.Equal(JsonValueKind.Null, initial.GetProperty("available").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/hub/update", null)).StatusCode);

        // Recusados: outra chave, assinatura de pacote do agente, versão antiga e pacote sem o painel.
        await AssertRefused(admin, ReleasePackages.HubPackage("9.1.0", ReleaseSigning.NewKeyPair().PrivateKeyFile), "Assinatura inválida");
        await AssertRefused(admin, ReleasePackages.HubPackage("9.1.0", _key.PrivateKeyFile, product: ReleaseProducts.Agent), "Assinatura inválida");
        await AssertRefused(admin, ReleasePackages.HubPackage("0.0.1", _key.PrivateKeyFile), "não é mais nova");
        await AssertRefused(admin, ReleasePackages.HubPackage("9.1.0", _key.PrivateKeyFile, [.. ReleasePackages.HubFiles.Where(f => f != "wwwroot/index.html")]), "wwwroot/index.html");

        var package = ReleasePackages.HubPackage("9.1.0", _key.PrivateKeyFile);
        var uploaded = await HubHarness.JsonAsync(await ReleasePackages.UploadAsync(admin, package, "/api/hub/update/package"));
        Assert.Equal("9.1.0", uploaded.GetProperty("available").GetProperty("version").GetString());
        Assert.Equal(package.Zip, await File.ReadAllBytesAsync(files.PackagePath("9.1.0")));

        var requested = await admin.PostAsync("/api/hub/update", null);
        Assert.Equal(HttpStatusCode.Accepted, requested.StatusCode);
        var pending = (await HubHarness.JsonAsync(requested)).GetProperty("pending");
        Assert.Equal(("9.1.0", "waiting", false, false), (pending.GetProperty("version").GetString(), pending.GetProperty("stage").GetString(), pending.GetProperty("automatic").GetBoolean(), pending.GetProperty("updaterLate").GetBoolean()));

        // O pedido leva o que o atualizador confere de novo por conta própria.
        var request = ReadJson<HubUpdateRequest>(files.Pending);
        Assert.Matches("^[0-9a-f]{32}$", request.RequestId);
        Assert.Equal(("9.1.0", HubUpdates.CurrentVersion, ReleasePackages.Sha(package.Zip), false), (request.Version, request.PreviousVersion, request.Sha256, request.Automatic));
        Assert.True(ReleaseSigning.Verify(_key.PublicKey, request.Version, request.Sha256, request.Signature, ReleaseProducts.Hub));

        Assert.Contains("Já há", await (await admin.PostAsync("/api/hub/update", null)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertRefused(admin, ReleasePackages.HubPackage("9.2.0", _key.PrivateKeyFile), "em andamento");

        // Sem a tarefa do atualizador, o painel avisa depois de 3 minutos.
        hub.Time.Advance(TimeSpan.FromMinutes(4));
        var late = await HubHarness.JsonAsync(await admin.GetAsync("/api/hub/update"));
        Assert.True(late.GetProperty("pending").GetProperty("updaterLate").GetBoolean());

        var cancelled = await HubHarness.JsonAsync(await admin.DeleteAsync("/api/hub/update"));
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("pending").ValueKind);
        Assert.False(File.Exists(files.Pending));
        Assert.Equal(1, await hub.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == "hub.update.request")));
        Assert.Equal(1, await hub.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == "hub.update.cancel")));
    }

    [Fact]
    public async Task Resultado_do_atualizador_fecha_o_pedido_e_nao_se_cancela_o_que_ja_comecou()
    {
        await using var hub = await StartAsync();
        var admin = await hub.AdminClientAsync();
        var updates = Updates(hub);
        await ReleasePackages.UploadAsync(admin, ReleasePackages.HubPackage("9.1.0", _key.PrivateKeyFile), "/api/hub/update/package");
        await admin.PostAsync("/api/hub/update", null);
        var request = ReadJson<HubUpdateRequest>(updates.Files.Pending);

        Directory.CreateDirectory(updates.Files.Updater);
        WriteJson(updates.Files.State, new HubUpdateState(request.RequestId, "9.1.0", HubUpdates.CurrentVersion, HubUpdateStages.Stopping, hub.Time.GetUtcNow()));
        var installing = await HubHarness.JsonAsync(await admin.GetAsync("/api/hub/update"));
        Assert.Equal("stopping", installing.GetProperty("pending").GetProperty("stage").GetString());
        Assert.Contains("já começou", await (await admin.DeleteAsync("/api/hub/update")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Resultado de outro pedido não conta.
        WriteJson(updates.Files.Result, new HubUpdateResult(Guid.NewGuid().ToString("N"), "9.1.0", "0.1.0", UpdateStages.Done, null, hub.Time.GetUtcNow()));
        await updates.TickAsync(CancellationToken.None);
        Assert.True(File.Exists(updates.Files.Pending));

        File.Delete(updates.Files.State);
        WriteJson(updates.Files.Result, new HubUpdateResult(request.RequestId, "9.1.0", HubUpdates.CurrentVersion, UpdateStages.Done, null, hub.Time.GetUtcNow()));
        await updates.TickAsync(CancellationToken.None);

        var status = await HubHarness.JsonAsync(await admin.GetAsync("/api/hub/update"));
        Assert.Equal(JsonValueKind.Null, status.GetProperty("pending").ValueKind);
        var last = status.GetProperty("last");
        Assert.Equal(("9.1.0", "done", false), (last.GetProperty("version").GetString(), last.GetProperty("outcome").GetString(), last.GetProperty("automatic").GetBoolean()));
        Assert.False(File.Exists(updates.Files.Pending));
        Assert.Equal(1, await hub.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == "hub.update.done")));
    }

    [Fact]
    public async Task De_madrugada_pede_sozinho_uma_vez_por_versao_e_o_pedido_sem_atualizador_vence()
    {
        await using var hub = await StartAsync(new DateTimeOffset(2026, 10, 24, 2, 58, 0, TimeSpan.FromHours(-3)));
        var admin = await hub.AdminClientAsync();
        var updates = Updates(hub);
        await ReleasePackages.UploadAsync(admin, ReleasePackages.HubPackage("9.1.0", _key.PrivateKeyFile), "/api/hub/update/package");

        await updates.TickAsync(CancellationToken.None);
        Assert.False(File.Exists(updates.Files.Pending));

        hub.Time.Advance(TimeSpan.FromMinutes(3));
        await updates.TickAsync(CancellationToken.None);
        Assert.True(ReadJson<HubUpdateRequest>(updates.Files.Pending).Automatic);

        // A tarefa do atualizador não existe: em 30 minutos o pedido vence e a mesma versão não é pedida de novo sozinha.
        hub.Time.Advance(TimeSpan.FromMinutes(31));
        await updates.TickAsync(CancellationToken.None);
        await updates.TickAsync(CancellationToken.None);
        Assert.False(File.Exists(updates.Files.Pending));
        var last = (await HubHarness.JsonAsync(await admin.GetAsync("/api/hub/update"))).GetProperty("last");
        Assert.Equal("failed", last.GetProperty("outcome").GetString());
        Assert.Contains("install-hub.ps1", last.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.True(last.GetProperty("automatic").GetBoolean());

        // Uma versão mais nova vai normalmente, a não ser com a madrugada desligada.
        await ReleasePackages.UploadAsync(admin, ReleasePackages.HubPackage("9.2.0", _key.PrivateKeyFile), "/api/hub/update/package");
        var off = await HubHarness.JsonAsync(await admin.PutAsJsonAsync("/api/hub/update/auto", new { enabled = false }));
        Assert.False(off.GetProperty("autoUpdate").GetBoolean());
        await updates.TickAsync(CancellationToken.None);
        Assert.False(File.Exists(updates.Files.Pending));

        await admin.PutAsJsonAsync("/api/hub/update/auto", new { enabled = true });
        await updates.TickAsync(CancellationToken.None);
        Assert.Equal("9.2.0", ReadJson<HubUpdateRequest>(updates.Files.Pending).Version);
    }

    [Fact]
    public async Task Versao_nova_subindo_pelo_atualizador_guarda_o_banco_e_confirma()
    {
        var requestId = Guid.NewGuid().ToString("N");
        await using var hub = await StartAsync(beforeStart: root =>
        {
            var files = new HubUpdatePaths(root);
            WriteJson(files.Pending, new HubUpdateRequest(requestId, HubUpdates.CurrentVersion, "0.0.9", new string('a', 64), "assinatura", DateTimeOffset.UtcNow, false));
            WriteJson(files.State, new HubUpdateState(requestId, HubUpdates.CurrentVersion, "0.0.9", HubUpdateStages.Confirming, DateTimeOffset.UtcNow));
        });
        var updates = Updates(hub);

        var confirmation = ReadJson<HubUpdateConfirmation>(updates.Files.Confirmed);
        Assert.Equal((requestId, HubUpdates.CurrentVersion), (confirmation.RequestId, confirmation.Version));
        Assert.True(Directory.Exists(updates.Files.DatabaseBackup(requestId)));
    }

    [Fact]
    public async Task Versao_que_voltou_devolve_o_banco_de_antes_ao_subir()
    {
        var saved = Path.Combine(_keyDirectory, "banco-antes.db");
        await using (var before = await StartAsync())
        {
            await before.WithDbAsync(async db =>
            {
                db.Settings.Add(new Setting { Key = "teste.marcador", Value = "antes" });
                await db.SaveChangesAsync();
                // O Hub de teste cadastra o admin de novo ao subir.
                await db.Users.ExecuteDeleteAsync();
                await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
            });
            SqliteConnection.ClearAllPools();
            File.Copy(before.Paths.Database, saved);
        }

        var requestId = Guid.NewGuid().ToString("N");
        await using var hub = await StartAsync(beforeStart: root =>
        {
            var files = new HubUpdatePaths(root);
            Directory.CreateDirectory(files.DatabaseBackup(requestId));
            File.Copy(saved, Path.Combine(files.DatabaseBackup(requestId), "marumbi.db"));
            WriteJson(files.Pending, new HubUpdateRequest(requestId, "9.1.0", HubUpdates.CurrentVersion, new string('a', 64), "assinatura", DateTimeOffset.UtcNow, true));
            WriteJson(files.Result, new HubUpdateResult(requestId, "9.1.0", HubUpdates.CurrentVersion, UpdateStages.RolledBack, "A versão nova não confirmou.", DateTimeOffset.UtcNow));
        });
        var updates = Updates(hub);

        Assert.Equal("antes", await hub.WithDbAsync(db => db.Settings.Where(s => s.Key == "teste.marcador").Select(s => s.Value).SingleAsync()));
        Assert.False(Directory.Exists(updates.Files.DatabaseBackup(requestId)));

        await updates.TickAsync(CancellationToken.None);
        Assert.False(File.Exists(updates.Files.Pending));
        Assert.Equal(1, await hub.WithDbAsync(db => db.AuditLog.CountAsync(a => a.Action == "hub.update.rolledBack")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_keyDirectory))
        {
            Directory.Delete(_keyDirectory, recursive: true);
        }
    }

    private Task<HubHarness> StartAsync(DateTimeOffset? now = null, Action<string>? beforeStart = null) =>
        HubHarness.StartAsync(now, configureServices: services => services.PostConfigure<HubOptions>(o => o.UpdatePublicKeyFile = KeyFile), beforeStart: beforeStart);

    private static HubUpdates Updates(HubHarness hub) => hub.App.Services.GetRequiredService<HubUpdates>();

    private static async Task AssertRefused(HttpClient admin, ReleasePackages.ReleaseFiles package, string reason)
    {
        var response = await ReleasePackages.UploadAsync(admin, package, "/api/hub/update/package");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        Assert.Contains(reason, text, StringComparison.Ordinal);
    }

    private static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), ContractJson.Options)!;

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, ContractJson.Options));
    }
}
