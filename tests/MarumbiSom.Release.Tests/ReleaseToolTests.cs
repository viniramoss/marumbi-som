using System.IO.Compression;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Release.Tests;

/// <summary>Ferramenta de chave e pacotes do agente (Etapa 6, D27 b).</summary>
public sealed class ReleaseToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "marumbi-release-tests", Guid.NewGuid().ToString("N"));
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public ReleaseToolTests()
    {
        Agent = Path.Combine(_root, "agente");
        Out = Path.Combine(_root, "pacotes");
        Key = Path.Combine(_root, "chave", "chave-privada.txt");
        Directory.CreateDirectory(Agent);
        // Qualquer assembly com a versão do Directory.Build.props serve de MarumbiSom.Agent.dll.
        File.Copy(typeof(ReleaseTool).Assembly.Location, Path.Combine(Agent, ReleaseTool.AgentDll));
        File.WriteAllText(Path.Combine(Agent, "MarumbiSom.Agent.exe"), "agente");
        File.WriteAllText(Path.Combine(Agent, "MarumbiSom.Updater.exe"), "updater");
        Directory.CreateDirectory(Path.Combine(Agent, "tools"));
        File.WriteAllText(Path.Combine(Agent, "tools", "mpv.exe"), "mpv");
        File.WriteAllText(Path.Combine(Agent, ReleaseSigning.PublicKeyFileName), "# cole aqui a chave pública\n");
    }

    private string Agent { get; }

    private string Out { get; }

    private string Key { get; }

    private string Version => ReleaseTool.DllVersion(Path.Combine(Agent, ReleaseTool.AgentDll));

    [Fact]
    public void Gera_a_chave_empacota_e_o_pacote_confere()
    {
        Assert.Equal(0, Run("new-key", "--out", Key, "--public-out", Path.Combine(Agent, ReleaseSigning.PublicKeyFileName)));
        var publicKey = ReleaseSigning.PublicKeyOf(File.ReadAllText(Key));
        Assert.Contains(publicKey, _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Nunca envie", File.ReadAllText(Key), StringComparison.Ordinal);
        Assert.Equal(publicKey, ReleaseSigning.ReadPublicKey(File.ReadAllText(Path.Combine(Agent, ReleaseSigning.PublicKeyFileName))));

        Assert.Equal(0, Run("pack", "--key", Key, "--agent", Agent, "--out", Out));

        var zip = Path.Combine(Out, $"MarumbiSom.Agent-{Version}.zip");
        var sig = Path.Combine(Out, $"MarumbiSom.Agent-{Version}.sig.json");
        var signature = JsonSerializer.Deserialize<ReleaseSignature>(File.ReadAllText(sig), ContractJson.Options)!;
        Assert.Equal(Version, signature.Version);
        Assert.True(ReleaseSigning.Verify(publicKey, signature.Version, signature.Sha256, signature.Signature));
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Contains("tools/mpv.exe", archive.Entries.Select(e => e.FullName.Replace('\\', '/')));
            Assert.Contains("MarumbiSom.Updater.exe", archive.Entries.Select(e => e.FullName));
        }

        Assert.Equal(0, Run("verify", "--zip", zip, "--sig", sig, "--public-key-file", Path.Combine(Agent, ReleaseSigning.PublicKeyFileName)));
        Assert.Equal(0, Run("public-key", "--key", Key));
        // Não sobrescreve a chave que já existe.
        Assert.Equal(1, Run("new-key", "--out", Key));
        Assert.Contains("já existe", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Recusa_versao_que_nao_e_a_do_build_e_pasta_sem_a_chave_certa()
    {
        Run("new-key", "--out", Key);

        Assert.Equal(1, Run("pack", "--key", Key, "--agent", Agent, "--out", Out));
        Assert.Contains("não tem chave", _error.ToString(), StringComparison.Ordinal);

        ReleaseTool.WritePublicKey(Path.Combine(Agent, ReleaseSigning.PublicKeyFileName), ReleaseSigning.NewKeyPair().PublicKey);
        Assert.Equal(1, Run("pack", "--key", Key, "--agent", Agent, "--out", Out));
        Assert.Contains("--allow-key-change", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, Run("pack", "--key", Key, "--agent", Agent, "--out", Out, "--allow-key-change"));
        Assert.Contains("passam a aceitar só pacotes assinados pela chave", _output.ToString(), StringComparison.Ordinal);

        ReleaseTool.WritePublicKey(Path.Combine(Agent, ReleaseSigning.PublicKeyFileName), ReleaseSigning.PublicKeyOf(File.ReadAllText(Key)));
        Assert.Equal(1, Run("pack", "--key", Key, "--agent", Agent, "--out", Out, "--version", "9.9.9"));
        Assert.Contains($"não é a do MarumbiSom.Agent.dll ({Version})", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Run("pack", "--key", Key, "--agent", Agent, "--out", Path.Combine(Agent, "saida")));
        File.Delete(Path.Combine(Agent, "MarumbiSom.Updater.exe"));
        Assert.Equal(1, Run("pack", "--key", Key, "--agent", Agent, "--out", Out));
        Assert.Contains("faltam MarumbiSom.Updater.exe", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Gera_os_pacotes_de_teste_do_aceite_da_etapa_6()
    {
        Run("new-key", "--out", Key, "--public-out", Path.Combine(Agent, ReleaseSigning.PublicKeyFileName));
        var publicKeyFile = Path.Combine(Agent, ReleaseSigning.PublicKeyFileName);

        Assert.Equal(0, Run("pack", "--key", Key, "--agent", Agent, "--out", Out, "--test-invalid-signature"));
        var invalid = Path.Combine(Out, $"MarumbiSom.Agent-{Version}-teste-assinatura-invalida");
        Assert.Equal(1, Run("verify", "--zip", invalid + ".zip", "--sig", invalid + ".sig.json", "--public-key-file", publicKeyFile));
        Assert.Contains("Assinatura inválida", _error.ToString(), StringComparison.Ordinal);

        Assert.Equal(1, Run("pack", "--key", Key, "--agent", Agent, "--out", Out, "--test-broken"));
        Assert.Equal(0, Run("pack", "--key", Key, "--agent", Agent, "--out", Out, "--test-broken", "--version", "0.9.1"));
        var broken = Path.Combine(Out, "MarumbiSom.Agent-0.9.1-teste-quebrado");
        Assert.Equal(0, Run("verify", "--zip", broken + ".zip", "--sig", broken + ".sig.json", "--public-key-file", publicKeyFile));
        using var archive = ZipFile.OpenRead(broken + ".zip");
        using var reader = new StreamReader(archive.GetEntry(ReleaseTool.AgentDll)!.Open());
        Assert.StartsWith("Pacote de teste quebrado", reader.ReadToEnd(), StringComparison.Ordinal);
        // A pasta do agente não muda.
        Assert.Equal(Version, ReleaseTool.DllVersion(Path.Combine(Agent, ReleaseTool.AgentDll)));
    }

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private int Run(params string[] args) => new ReleaseTool(_output, _error).Run(args);
}
