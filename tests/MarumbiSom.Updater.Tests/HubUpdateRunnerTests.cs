using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarumbiSom.Contracts.Releases;
using Microsoft.Extensions.Time.Testing;

namespace MarumbiSom.Updater.Tests;

/// <summary>Atualização do Hub pela tarefa agendada como SYSTEM (D32 c a e).</summary>
public sealed class HubUpdateRunnerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "marumbi-hub-updater-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 27, 6, 0, 0, TimeSpan.Zero));
    private readonly (string PrivateKeyFile, string PublicKey) _key = ReleaseSigning.NewKeyPair();

    public HubUpdateRunnerTests()
    {
        Paths = new HubUpdatePaths(_root);
        Directory.CreateDirectory(Paths.DownloadDirectory);
        Directory.CreateDirectory(Paths.Updater);
        Write(Paths.App, "MarumbiSom.Hub.exe", "hub 0.6.10");
        File.WriteAllBytes(Path.Combine(Paths.App, HubUpdatePaths.HubDll), HubDll("0.6.10"));
        Write(Paths.App, ReleaseSigning.PublicKeyFileName, $"# chave de teste\n{_key.PublicKey}\n");
        Write(Paths.App, "wwwroot/antigo.js", "painel antigo");
        Write(Paths.App, "appsettings.Production.json", "{}");
    }

    private HubUpdatePaths Paths { get; }

    [Fact]
    public async Task Versao_nova_assinada_que_confirma_fica_instalada_e_o_pedido_nao_se_repete()
    {
        var request = Request("0.6.12", Package("0.6.12"));
        var service = new FakeService(this) { Confirm = true };

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal((UpdateStages.Done, null, "0.6.10"), (result!.Outcome, result.Error, result.PreviousVersion));
        Assert.Equal(["stop", "kill", "start"], service.Calls);
        Assert.Equal("hub 0.6.12", Read(Paths.App, "MarumbiSom.Hub.exe"));
        Assert.Equal("0.6.12", HubUpdateRunner.InstalledVersion(Paths.App));
        Assert.True(File.Exists(Path.Combine(Paths.App, "wwwroot", "novo.js")));
        Assert.False(File.Exists(Path.Combine(Paths.App, "wwwroot", "antigo.js")));
        Assert.Equal("{}", Read(Paths.App, "appsettings.Production.json"));
        Assert.Equal("hub 0.6.10", Read(Paths.Backup, "MarumbiSom.Hub.exe"));
        Assert.Equal(request.RequestId, ReadResult().RequestId);
        Assert.False(File.Exists(Paths.State));
        Assert.False(File.Exists(Paths.Package));
        Assert.False(Directory.Exists(Paths.Staging));
        // O Updater não apaga nada de data\: o Hub novo apaga o pedido quando lê o resultado.
        Assert.True(File.Exists(Paths.Pending));

        Assert.Null(await Runner(service).RunAsync(CancellationToken.None));
        Assert.Equal(3, service.Calls.Count);
    }

    [Fact]
    public async Task Versao_quebrada_que_nao_confirma_volta_sozinha_e_o_resultado_vem_antes_de_iniciar()
    {
        Request("0.6.12", Package("0.6.12"));
        var service = new FakeService(this) { Confirm = false };

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateStages.RolledBack, result!.Outcome);
        Assert.Contains("não confirmou", result.Error, StringComparison.Ordinal);
        Assert.Contains("0.6.10 foi restaurada, com o banco de antes", result.Error, StringComparison.Ordinal);
        Assert.Equal(["stop", "kill", "start", "stop", "kill", "start"], service.Calls);
        Assert.Equal("hub 0.6.10", Read(Paths.App, "MarumbiSom.Hub.exe"));
        Assert.Equal("0.6.10", HubUpdateRunner.InstalledVersion(Paths.App));
        Assert.True(File.Exists(Path.Combine(Paths.App, "wwwroot", "antigo.js")));
        // O Hub restaurado acha o resultado ao subir e devolve o banco da cópia.
        Assert.Equal(UpdateStages.RolledBack, service.ResultOnLastStart?.Outcome);
        Assert.False(File.Exists(Paths.State));
    }

    [Fact]
    public async Task Assinatura_de_outra_chave_ou_de_pacote_do_agente_e_recusada_sem_parar_o_hub()
    {
        var package = Package("0.6.12");
        var service = new FakeService(this) { Confirm = true };

        Request("0.6.12", package, signature: ReleaseSigning.Sign(ReleaseSigning.NewKeyPair().PrivateKeyFile, "0.6.12", Sha(package), ReleaseProducts.Hub));
        var otherKey = await Runner(service).RunAsync(CancellationToken.None);
        Request("0.6.12", package, signature: ReleaseSigning.Sign(_key.PrivateKeyFile, "0.6.12", Sha(package), ReleaseProducts.Agent));
        var agentSignature = await Runner(service).RunAsync(CancellationToken.None);

        Assert.All([otherKey, agentSignature], r => Assert.StartsWith("Assinatura inválida", r!.Error, StringComparison.Ordinal));
        Assert.Empty(service.Calls);
        Assert.Equal("hub 0.6.10", Read(Paths.App, "MarumbiSom.Hub.exe"));
        Assert.False(File.Exists(Paths.Package));
    }

    [Fact]
    public async Task Versao_igual_ou_antiga_pacote_trocado_e_pedido_vencido_sao_recusados()
    {
        var service = new FakeService(this) { Confirm = true };

        Request("0.6.10", Package("0.6.10"));
        Assert.Equal("A versão 0.6.10 não é mais nova que a instalada (0.6.10).", (await Runner(service).RunAsync(CancellationToken.None))!.Error);

        Request("0.6.12", Package("0.6.12"));
        await File.WriteAllBytesAsync(Paths.PackagePath("0.6.12"), Package("0.6.12", "outro conteúdo"));
        Assert.Contains("SHA-256 diferente", (await Runner(service).RunAsync(CancellationToken.None))!.Error, StringComparison.Ordinal);

        Request("0.6.12", Package("0.6.12"), createdAt: _time.GetUtcNow().AddHours(-1));
        Assert.Contains("venceu", (await Runner(service).RunAsync(CancellationToken.None))!.Error, StringComparison.Ordinal);

        // Assinado como 0.6.13, mas o Hub de dentro é 0.6.12.
        Request("0.6.13", Package("0.6.12"));
        Assert.Equal("O pacote assinado como 0.6.13 tem o Hub 0.6.12.", (await Runner(service).RunAsync(CancellationToken.None))!.Error);

        File.WriteAllText(Paths.Pending, """{ "requestId": "../../fora", "version": "0.6.12" }""");
        Assert.Contains("identificador", (await Runner(service).RunAsync(CancellationToken.None))!.Error, StringComparison.Ordinal);
        // O mesmo pedido ruim não é registrado de novo a cada minuto.
        Assert.Null(await Runner(service).RunAsync(CancellationToken.None));
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task Pedido_ou_pacote_por_link_nao_e_seguido()
    {
        var service = new FakeService(this) { Confirm = true };
        var package = Package("0.6.12");
        var elsewhere = Path.Combine(_root, "fora");
        Directory.CreateDirectory(elsewhere);

        // O pacote numa pasta qualquer do PC, com a pasta de download trocada por um link para ela.
        Directory.Delete(Paths.DownloadDirectory);
        await File.WriteAllBytesAsync(Path.Combine(elsewhere, "MarumbiSom.Hub-0.6.12.zip"), package);
        Directory.CreateSymbolicLink(Paths.DownloadDirectory, elsewhere);
        Request("0.6.12", package, writePackage: false);
        var linkedFolder = await Runner(service).RunAsync(CancellationToken.None);
        Assert.Contains("não segue links", linkedFolder!.Error, StringComparison.Ordinal);

        // O próprio pedido como link para outro arquivo.
        var target = Path.Combine(elsewhere, "pedido.json");
        File.Move(Paths.Pending, target);
        File.CreateSymbolicLink(Paths.Pending, target);
        var linkedRequest = await Runner(service).RunAsync(CancellationToken.None);
        Assert.Contains("não segue links", linkedRequest!.Error, StringComparison.Ordinal);

        Assert.Empty(service.Calls);
        Assert.Equal("hub 0.6.10", Read(Paths.App, "MarumbiSom.Hub.exe"));
    }

    [Fact]
    public async Task Troca_interrompida_no_meio_e_desfeita_na_execucao_seguinte()
    {
        UpdateRunner.CopyTree(Paths.App, Paths.Backup);
        Write(Paths.App, "MarumbiSom.Hub.exe", "meio trocado");
        File.Delete(Path.Combine(Paths.App, "wwwroot", "antigo.js"));
        State(HubUpdateStages.Installing);
        var service = new FakeService(this);

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateStages.RolledBack, result!.Outcome);
        Assert.Contains("interrompida no meio da troca", result.Error, StringComparison.Ordinal);
        Assert.Equal("hub 0.6.10", Read(Paths.App, "MarumbiSom.Hub.exe"));
        Assert.True(File.Exists(Path.Combine(Paths.App, "wwwroot", "antigo.js")));
        Assert.Equal(["stop", "kill", "start"], service.Calls);
        Assert.False(File.Exists(Paths.State));
    }

    [Fact]
    public async Task Troca_interrompida_antes_de_trocar_so_inicia_o_hub()
    {
        State(HubUpdateStages.Stopping);
        var service = new FakeService(this);

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateStages.Failed, result!.Outcome);
        Assert.Equal(["start"], service.Calls);
        Assert.Equal("hub 0.6.10", Read(Paths.App, "MarumbiSom.Hub.exe"));
    }

    [Fact]
    public async Task Sem_pedido_nao_faz_nada()
    {
        var service = new FakeService(this);
        Assert.Null(await Runner(service).RunAsync(CancellationToken.None));
        Assert.Empty(service.Calls);
        Assert.False(File.Exists(Paths.Result));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private HubUpdateRunner Runner(FakeService service) =>
        new(Paths, service, _time, _log.Add) { ConfirmTimeout = TimeSpan.Zero };

    private HubUpdateRequest Request(string version, byte[] package, string? signature = null, DateTimeOffset? createdAt = null, bool writePackage = true)
    {
        if (writePackage)
        {
            File.WriteAllBytes(Paths.PackagePath(version), package);
        }

        var sha = Sha(package);
        var request = new HubUpdateRequest(
            Guid.NewGuid().ToString("N"), version, "0.6.10", sha, signature ?? ReleaseSigning.Sign(_key.PrivateKeyFile, version, sha, ReleaseProducts.Hub),
            createdAt ?? _time.GetUtcNow(), Automatic: false);
        File.WriteAllText(Paths.Pending, JsonSerializer.Serialize(request, Json));
        return request;
    }

    private void State(string stage) =>
        File.WriteAllText(Paths.State, JsonSerializer.Serialize(new HubUpdateState(Guid.NewGuid().ToString("N"), "0.6.12", "0.6.10", stage, _time.GetUtcNow()), Json));

    private static byte[] Package(string version, string exe = "")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Entry(zip, "MarumbiSom.Hub.exe", Encoding.UTF8.GetBytes(exe.Length > 0 ? exe : $"hub {version}"));
            Entry(zip, HubUpdatePaths.HubDll, HubDll(version));
            Entry(zip, "wwwroot/novo.js", "painel novo"u8.ToArray());
        }

        return buffer.ToArray();
    }

    private static void Entry(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(content);
    }

    /// <summary>Um assembly mínimo com a versão pedida, como o MarumbiSom.Hub.dll que o CI gera.</summary>
    private static byte[] HubDll(string version)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName("MarumbiSom.Hub") { Version = Version.Parse(version + ".0") }, typeof(object).Assembly);
        builder.DefineDynamicModule("MarumbiSom.Hub").DefineType("Marcador", TypeAttributes.Public).CreateType();
        using var stream = new MemoryStream();
        builder.Save(stream);
        return stream.ToArray();
    }

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));

    private HubUpdateResult ReadResult() => JsonSerializer.Deserialize<HubUpdateResult>(File.ReadAllText(Paths.Result), Json)!;

    /// <summary>SCM falso: ao iniciar, o "Hub novo" confirma o pedido em <c>data\updates</c> (se <see cref="Confirm"/>).</summary>
    private sealed class FakeService(HubUpdateRunnerTests owner) : IServiceControl
    {
        public List<string> Calls { get; } = [];

        public bool Confirm { get; init; }

        public HubUpdateResult? ResultOnLastStart { get; private set; }

        public Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal("MarumbiSom.Hub", serviceName);
            Calls.Add("stop");
            return Task.CompletedTask;
        }

        public Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            ResultOnLastStart = File.Exists(owner.Paths.Result) ? owner.ReadResult() : null;
            var installed = HubUpdateRunner.InstalledVersion(owner.Paths.App);
            if (Confirm && JsonSerializer.Deserialize<HubUpdateRequest>(File.ReadAllText(owner.Paths.Pending), Json) is { } request && request.Version == installed)
            {
                File.WriteAllText(owner.Paths.Confirmed, JsonSerializer.Serialize(new HubUpdateConfirmation(request.RequestId, installed, DateTimeOffset.UtcNow), Json));
            }

            return Task.CompletedTask;
        }

        public void KillLeftovers(string programDirectory)
        {
            Assert.Equal(owner.Paths.App, programDirectory);
            Calls.Add("kill");
        }
    }
}
