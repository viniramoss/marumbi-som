using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Updater.Tests;

/// <summary>Instalação pelo Updater e a volta automática (Etapa 6, seção 12.2, D27 f).</summary>
public sealed class UpdateRunnerTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "marumbi-updater-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public UpdateRunnerTests()
    {
        Paths = new UpdatePaths(Path.Combine(_root, "dados"));
        Program = Path.Combine(_root, "programa");
        Directory.CreateDirectory(Paths.DownloadDirectory);
        Write(Program, "MarumbiSom.Agent.exe", "agente 0.6.10");
        Write(Program, "Antigo.dll", "só na versão antiga");
        Write(Program, "wwwroot/antigo.js", "antigo");
        Write(Program, "tools/mpv.exe", "mpv instalado à parte");
        Write(Program, "appsettings.Production.json", "{}");
    }

    private UpdatePaths Paths { get; }

    private string Program { get; }

    [Fact]
    public async Task Versao_nova_que_confirma_fica_instalada_e_a_antiga_vai_para_o_backup()
    {
        var pending = Pending(Package(("MarumbiSom.Agent.exe", "agente 0.6.12"), ("Novo.dll", "novo"), ("wwwroot/novo.js", "novo")));
        var service = new FakeService(this) { ConfirmAs = "0.6.12" };

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateStages.Done, result!.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(["stop", "kill", "start"], service.Calls);
        Assert.Equal("agente 0.6.12", Read(Program, "MarumbiSom.Agent.exe"));
        Assert.True(File.Exists(Path.Combine(Program, "Novo.dll")));
        Assert.False(File.Exists(Path.Combine(Program, "Antigo.dll")));
        Assert.False(File.Exists(Path.Combine(Program, "wwwroot", "antigo.js")));
        Assert.Equal("mpv instalado à parte", Read(Program, "tools/mpv.exe"));
        Assert.Equal("{}", Read(Program, "appsettings.Production.json"));
        Assert.Equal("agente 0.6.10", Read(Paths.Backup, "MarumbiSom.Agent.exe"));
        Assert.Equal(UpdateStages.Done, ReadResult().Outcome);
        Assert.False(File.Exists(Paths.Pending));
        Assert.False(File.Exists(Paths.Confirmed));
        Assert.False(Directory.Exists(Paths.Staging));
        Assert.False(File.Exists(pending.PackagePath));
    }

    [Fact]
    public async Task Versao_quebrada_que_nao_confirma_e_revertida_sozinha()
    {
        Pending(Package(("MarumbiSom.Agent.exe", "agente 0.6.12 quebrado"), ("Novo.dll", "novo")));
        // O agente antigo confirmando a versão dele não conta.
        var service = new FakeService(this) { ConfirmAs = "0.6.10" };

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateStages.RolledBack, result!.Outcome);
        Assert.Contains("não confirmou", result.Error, StringComparison.Ordinal);
        Assert.Contains("0.6.10 foi restaurada", result.Error, StringComparison.Ordinal);
        Assert.Equal(["stop", "kill", "start", "stop", "kill", "start"], service.Calls);
        Assert.Equal("agente 0.6.10", Read(Program, "MarumbiSom.Agent.exe"));
        Assert.True(File.Exists(Path.Combine(Program, "Antigo.dll")));
        Assert.True(File.Exists(Path.Combine(Program, "wwwroot", "antigo.js")));
        Assert.False(File.Exists(Path.Combine(Program, "Novo.dll")));
        // O resultado já está gravado quando o agente restaurado sobe.
        Assert.Equal(UpdateStages.RolledBack, service.ResultOnLastStart?.Outcome);
        Assert.False(File.Exists(Paths.Pending));
    }

    [Fact]
    public async Task Servico_que_nao_sobe_tambem_e_revertido()
    {
        Pending(Package(("MarumbiSom.Agent.exe", "agente 0.6.12")));
        var service = new FakeService(this) { FailStarts = 1 };

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateStages.RolledBack, result!.Outcome);
        Assert.Contains("Falha ao instalar", result.Error, StringComparison.Ordinal);
        Assert.Equal("agente 0.6.10", Read(Program, "MarumbiSom.Agent.exe"));
    }

    [Fact]
    public async Task Pacote_com_caminho_que_sai_da_pasta_e_recusado_sem_parar_a_loja()
    {
        Pending(Package(("MarumbiSom.Agent.exe", "agente 0.6.12"), ("../../fora.txt", "zip slip")));
        var service = new FakeService(this);

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateStages.Failed, result!.Outcome);
        Assert.Contains("sai da pasta", result.Error, StringComparison.Ordinal);
        Assert.Empty(service.Calls);
        Assert.False(File.Exists(Path.Combine(_root, "fora.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "dados", "fora.txt")));
        Assert.Equal("agente 0.6.10", Read(Program, "MarumbiSom.Agent.exe"));
    }

    [Fact]
    public async Task Pacote_trocado_depois_de_conferido_ou_sem_o_agente_e_recusado()
    {
        var pending = Pending(Package(("MarumbiSom.Agent.exe", "agente 0.6.12")));
        await File.WriteAllBytesAsync(pending.PackagePath, Package(("MarumbiSom.Agent.exe", "outro conteúdo")));
        var service = new FakeService(this);

        var changed = await Runner(service).RunAsync(CancellationToken.None);
        Assert.Contains("SHA-256", changed!.Error, StringComparison.Ordinal);

        Pending(Package(("Outro.exe", "sem agente")));
        var missing = await Runner(service).RunAsync(CancellationToken.None);
        Assert.Equal((UpdateStages.Failed, "O pacote não tem o MarumbiSom.Agent.exe."), (missing!.Outcome, missing.Error));
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task Servico_que_nao_para_desiste_e_deixa_a_versao_atual()
    {
        Pending(Package(("MarumbiSom.Agent.exe", "agente 0.6.12")));
        var service = new FakeService(this) { FailStop = true };

        var result = await Runner(service).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateStages.Failed, result!.Outcome);
        Assert.Contains("parar o serviço", result.Error, StringComparison.Ordinal);
        Assert.Equal(["stop", "start"], service.Calls);
        Assert.Equal("agente 0.6.10", Read(Program, "MarumbiSom.Agent.exe"));
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

    private UpdateRunner Runner(FakeService service) =>
        new(Paths, service, TimeProvider.System, _log.Add) { ConfirmTimeout = TimeSpan.FromMilliseconds(400), PollInterval = TimeSpan.FromMilliseconds(20) };

    private PendingUpdate Pending(byte[] package)
    {
        var path = Paths.PackagePath("0.6.12");
        File.WriteAllBytes(path, package);
        var pending = new PendingUpdate(
            "0.6.12", "0.6.10", path, Convert.ToHexStringLower(SHA256.HashData(package)), Program, "MarumbiSom.Agent", DateTimeOffset.UtcNow);
        File.WriteAllText(Paths.Pending, JsonSerializer.Serialize(pending, Json));
        return pending;
    }

    private static byte[] Package(params (string Name, string Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));

    private UpdateResult ReadResult() => JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(Paths.Result), Json)!;

    /// <summary>SCM falso: ao iniciar, o "agente" da pasta grava o confirmed.json (se <see cref="ConfirmAs"/>).</summary>
    private sealed class FakeService(UpdateRunnerTests owner) : IServiceControl
    {
        public List<string> Calls { get; } = [];

        public string? ConfirmAs { get; init; }

        public bool FailStop { get; init; }

        public int FailStarts { get; set; }

        public UpdateResult? ResultOnLastStart { get; private set; }

        public Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("stop");
            return FailStop ? throw new InvalidOperationException("tempo esgotado") : Task.CompletedTask;
        }

        public Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("start");
            ResultOnLastStart = File.Exists(owner.Paths.Result) ? owner.ReadResult() : null;
            if (FailStarts > 0)
            {
                FailStarts--;
                throw new InvalidOperationException("o serviço não iniciou");
            }

            if (ConfirmAs is not null)
            {
                File.WriteAllText(owner.Paths.Confirmed, JsonSerializer.Serialize(new UpdateConfirmation(ConfirmAs, DateTimeOffset.UtcNow), Json));
            }

            return Task.CompletedTask;
        }

        public void KillLeftovers(string programDirectory) => Calls.Add("kill");
    }
}
