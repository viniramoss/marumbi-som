using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Release;

/// <summary>
/// Chave de assinatura e pacotes do agente e do Hub (D27 b, D32 a), no PC do administrador ou no CI com a chave do
/// secret (seção 12.1). Chamado pelos scripts <c>new-signing-key.ps1</c> e <c>sign-release.ps1</c> e pelo CI.
/// </summary>
internal sealed class ReleaseTool(TextWriter output, TextWriter error)
{
    public const string AgentDll = "MarumbiSom.Agent.dll";
    public const string HubDll = "MarumbiSom.Hub.dll";
    public const string HubSettingsFile = "appsettings.json";

    private static readonly PackageKind AgentPackage = new(
        ReleaseProducts.Agent, "MarumbiSom.Agent", AgentDll, "do agente",
        ["MarumbiSom.Agent.exe", AgentDll, "MarumbiSom.Updater.exe", ReleaseSigning.PublicKeyFileName]);

    /// <summary>O pacote do Hub leva o Updater e a chave pública (que a tarefa do atualizador usa) e o painel (D32 a).</summary>
    private static readonly PackageKind HubPackage = new(
        ReleaseProducts.Hub, "MarumbiSom.Hub", HubDll, "do Hub",
        ["MarumbiSom.Hub.exe", HubDll, "MarumbiSom.Updater.exe", ReleaseSigning.PublicKeyFileName, "wwwroot/index.html", "tools/ffmpeg.exe", "tools/ffprobe.exe", "scripts/install-hub.ps1"]);

    private static readonly JsonSerializerOptions Indented = new(ContractJson.Options) { WriteIndented = true };

    public const string Usage = """
        MarumbiSom.Release: chave de assinatura e pacotes de atualização do agente e do Hub (seção 12.2, D32).

          new-key --out <arquivo> [--public-out <release-public-key.txt>]
              Gera o par de chaves. A privada vai para <arquivo> (guarde só no seu PC); a pública aparece na tela e,
              com --public-out, é gravada no release-public-key.txt de uma pasta do agente.

          public-key --key <arquivo>
              Mostra a chave pública de uma chave privada.

          pack --key <arquivo> (--agent <pasta do agente publicado> | --hub <pasta do Hub publicado>)
               [--version <0.6.41>] [--out <pasta>] [--allow-key-change] [--test-invalid-signature] [--test-broken]
              Gera MarumbiSom.Agent-<versão>.zip (ou MarumbiSom.Hub-<versão>.zip) e .sig.json para enviar no painel. A
              versão é a do MarumbiSom.Agent.dll (ou MarumbiSom.Hub.dll).
              --test-invalid-signature: assina com uma chave descartável (o pacote precisa ser recusado); aceita
                  qualquer --version, para não ocupar no Hub a versão do pacote bom.
              --test-broken: o Updater precisa reverter. No agente, estraga o .dll e usa a --version pedida; no Hub, usa a
                  versão do .dll e põe no appsettings.json um endereço que derruba a partida depois de abrir o banco.

          verify --zip <pacote.zip> --sig <pacote.sig.json> (--public-key <base64> | --public-key-file <arquivo>) [--hub]
              Confere SHA-256 e assinatura de um pacote (--hub para um pacote do Hub).
        """;

    public int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var options = Options.Parse(args[1..]);
            return args[0] switch
            {
                "new-key" => NewKey(options),
                "public-key" => PublicKey(options),
                "pack" => Pack(options),
                "verify" => Verify(options),
                _ => throw new ToolException($"Comando desconhecido: {args[0]}.\n\n{Usage}"),
            };
        }
        catch (ToolException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or BadImageFormatException)
        {
            error.WriteLine($"Erro: {ex.Message}");
            return 1;
        }
    }

    private int NewKey(Options options)
    {
        var path = options.Required("out");
        if (File.Exists(path))
        {
            throw new ToolException($"{path} já existe. Para não perder a chave atual, apague ou escolha outro arquivo.");
        }

        var (privateKey, publicKey) = ReleaseSigning.NewKeyPair();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, $"""
            # Chave PRIVADA de assinatura das atualizações do Marumbi Som (seção 12.1).
            # Guarde só neste PC e numa cópia offline (pendrive no cofre). Nunca envie por mensagem nem coloque no Git,
            # no Hub ou na nuvem. Quem tiver este arquivo consegue instalar programas nos PCs das lojas.
            {privateKey}

            """, new UTF8Encoding(false));
        output.WriteLine($"Chave privada gravada em {Path.GetFullPath(path)}");
        if (options.Optional("public-out") is { } publicOut)
        {
            WritePublicKey(publicOut, publicKey);
            output.WriteLine($"Chave pública gravada em {Path.GetFullPath(publicOut)}");
        }

        output.WriteLine();
        output.WriteLine("Chave pública (pode ser compartilhada; vai no release-public-key.txt do agente):");
        output.WriteLine(publicKey);
        return 0;
    }

    private int PublicKey(Options options)
    {
        output.WriteLine(ReleaseSigning.PublicKeyOf(ReadKey(options.Required("key"))));
        return 0;
    }

    private int Pack(Options options)
    {
        var privateKey = ReadKey(options.Required("key"));
        var (kind, folder) = (options.Optional("agent"), options.Optional("hub")) switch
        {
            ({ } agentFolder, null) => (AgentPackage, agentFolder),
            (null, { } hubFolder) => (HubPackage, hubFolder),
            _ => throw new ToolException("Informe a pasta publicada com --agent ou com --hub (uma das duas)."),
        };
        var source = Path.GetFullPath(folder);
        var testInvalid = options.Flag("test-invalid-signature");
        var testBroken = options.Flag("test-broken");
        var missing = kind.RequiredFiles.Where(f => !File.Exists(Path.Combine(source, f))).ToList();
        if (missing.Count > 0)
        {
            throw new ToolException($"A pasta {source} não é a {kind.Label} publicado: faltam {string.Join(", ", missing)}.");
        }

        var dllVersion = DllVersion(Path.Combine(source, kind.Dll));
        var version = options.Optional("version") ?? dllVersion;
        if (!ReleaseSigning.IsVersion(version))
        {
            throw new ToolException($"Versão inválida: {version}. Use três números, como 0.6.41.");
        }

        if (testBroken && kind == HubPackage)
        {
            // O atualizador do Hub confere a versão do .dll; o Hub quebrado é o de verdade com um ajuste que derruba a
            // partida depois de abrir o banco (assim a cópia e a devolução do banco também são testadas).
            if (version != dllVersion)
            {
                throw new ToolException($"No Hub, --test-broken usa a versão do {kind.Dll} ({dllVersion}): use um build mais novo que o instalado, sem --version.");
            }
        }
        else if (testBroken)
        {
            if (options.Optional("version") is null || version == dllVersion)
            {
                throw new ToolException($"--test-broken precisa de uma --version diferente da {kind.Label} ({dllVersion}) e mais nova que a instalada.");
            }
        }
        else if (!testInvalid && version != dllVersion)
        {
            throw new ToolException($"A versão pedida ({version}) não é a do {kind.Dll} ({dllVersion}). Gere o pacote a partir do build dessa versão.");
        }

        var signingKey = ReleaseSigning.PublicKeyOf(privateKey);
        var packagedKey = ReleaseSigning.ReadPublicKey(File.ReadAllText(Path.Combine(source, ReleaseSigning.PublicKeyFileName)));
        if (packagedKey is null)
        {
            throw new ToolException(
                $"O {ReleaseSigning.PublicKeyFileName} da pasta {kind.Label} não tem chave. Coloque a chave pública no repositório " +
                "(src/MarumbiSom.Agent/release-public-key.txt) e use um build novo; sem ela as próximas atualizações seriam recusadas.");
        }

        if (packagedKey != signingKey)
        {
            if (!options.Flag("allow-key-change"))
            {
                throw new ToolException(
                    $"O {ReleaseSigning.PublicKeyFileName} da pasta {kind.Label} tem outra chave ({packagedKey}), não a desta assinatura ({signingKey}). " +
                    "Quem for atualizado com este pacote só aceitaria pacotes assinados pela outra chave. Se a troca de chave for intencional, use --allow-key-change.");
            }

            output.WriteLine($"Atenção: quem for atualizado com este pacote passa a aceitar só pacotes assinados pela chave {packagedKey}.");
        }

        var outDirectory = Path.GetFullPath(options.Optional("out") ?? Directory.GetCurrentDirectory());
        if (outDirectory.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || outDirectory == source)
        {
            throw new ToolException($"A pasta de saída não pode ficar dentro da pasta {kind.Label}.");
        }

        Directory.CreateDirectory(outDirectory);
        var suffix = testInvalid ? "-teste-assinatura-invalida" : testBroken ? "-teste-quebrado" : string.Empty;
        var zipPath = Path.Combine(outDirectory, $"{kind.Name}-{version}{suffix}.zip");
        var signaturePath = Path.Combine(outDirectory, $"{kind.Name}-{version}{suffix}.sig.json");
        File.Delete(zipPath);
        ZipFile.CreateFromDirectory(source, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        if (testBroken && kind == HubPackage)
        {
            BreakHubSettings(zipPath);
        }
        else if (testBroken)
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
            zip.GetEntry(kind.Dll)!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(kind.Dll).Open());
            writer.Write("Pacote de teste quebrado de propósito (D27 b): este programa não sobe e o Updater precisa voltar a versão anterior.");
        }

        string sha;
        using (var stream = File.OpenRead(zipPath))
        {
            sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        var signature = ReleaseSigning.Sign(testInvalid ? ReleaseSigning.NewKeyPair().PrivateKeyFile : privateKey, version, sha, kind.Product);
        File.WriteAllText(signaturePath, JsonSerializer.Serialize(new ReleaseSignature(version, sha, signature), Indented), new UTF8Encoding(false));

        output.WriteLine($"Pacote:     {zipPath} ({new FileInfo(zipPath).Length / (1024.0 * 1024):0.0} MB)");
        output.WriteLine($"Assinatura: {signaturePath}");
        output.WriteLine($"Versão {version}, SHA-256 {sha}");
        var hub = kind == HubPackage;
        if (testInvalid)
        {
            output.WriteLine($"PACOTE DE TESTE: assinado com uma chave descartável. {(hub ? "O Hub" : "A loja")} precisa recusar (\"Assinatura inválida\").");
        }
        else if (testBroken)
        {
            output.WriteLine(hub
                ? "PACOTE DE TESTE: o Hub desta versão abre o banco e cai antes de confirmar. O atualizador precisa voltar sozinho para a versão anterior, com o banco de antes, em até ~4 minutos."
                : "PACOTE DE TESTE: o agente desta versão não sobe. A loja precisa voltar sozinha para a versão anterior em até ~3 minutos.");
        }
        else
        {
            output.WriteLine(hub
                ? "Envie os dois arquivos em Configurações > Atualização do Hub > Enviar pacote, no painel."
                : "Envie os dois arquivos em Configurações > Versão do agente para distribuição, no painel.");
        }

        return 0;
    }

    /// <summary>
    /// Pacote do Hub de teste (D32): o <c>appsettings.json</c> ganha um endereço com porta inválida. O Hub abre o banco
    /// (guardando a cópia da atualização), não consegue escutar e cai antes de confirmar.
    /// </summary>
    private static void BreakHubSettings(string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        JsonObject settings;
        if (zip.GetEntry(HubSettingsFile) is { } entry)
        {
            using (var reader = entry.Open())
            {
                settings = JsonNode.Parse(reader, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })?.AsObject()
                    ?? throw new ToolException($"O {HubSettingsFile} do Hub está vazio.");
            }

            entry.Delete();
        }
        else
        {
            settings = [];
        }

        settings["Kestrel"] = new JsonObject
        {
            ["Endpoints"] = new JsonObject { ["TesteQuebrado"] = new JsonObject { ["Url"] = "http://127.0.0.1:99999" } },
        };
        using var writer = new StreamWriter(zip.CreateEntry(HubSettingsFile).Open(), new UTF8Encoding(false));
        writer.Write(settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private int Verify(Options options)
    {
        var zipPath = options.Required("zip");
        var signature = JsonSerializer.Deserialize<ReleaseSignature>(File.ReadAllText(options.Required("sig")), ContractJson.Options)
            ?? throw new ToolException("Arquivo de assinatura vazio.");
        var publicKey = options.Optional("public-key")
            ?? ReleaseSigning.ReadPublicKey(File.ReadAllText(options.Optional("public-key-file") ?? throw new ToolException("Informe --public-key ou --public-key-file.")))
            ?? throw new ToolException("O arquivo da chave pública não tem chave.");
        string sha;
        using (var stream = File.OpenRead(zipPath))
        {
            sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        if (!string.Equals(sha, signature.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine($"SHA-256 diferente: o pacote tem {sha}, a assinatura diz {signature.Sha256}.");
            return 1;
        }

        if (!ReleaseSigning.Verify(publicKey, signature.Version, sha, signature.Signature, options.Flag("hub") ? ReleaseProducts.Hub : ReleaseProducts.Agent))
        {
            error.WriteLine("Assinatura inválida para esta chave pública.");
            return 1;
        }

        output.WriteLine($"OK: versão {signature.Version} assinada por esta chave.");
        return 0;
    }

    /// <summary>Versão do build (<c>-p:Version=0.6.41</c> no CI vira AssemblyVersion 0.6.41.0).</summary>
    internal static string DllVersion(string path)
    {
        var version = AssemblyName.GetAssemblyName(path).Version ?? throw new ToolException($"{path} não tem versão.");
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    internal static void WritePublicKey(string path, string publicKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, $"""
            # Chave pública Ed25519 das atualizações do agente (seção 12.2, D27 c).
            {publicKey}

            """, new UTF8Encoding(false));
    }

    private static string ReadKey(string path)
    {
        if (!File.Exists(path))
        {
            throw new ToolException($"Chave privada não encontrada: {path}. Gere com new-signing-key.ps1.");
        }

        return File.ReadAllText(path);
    }

    /// <param name="Label">Complemento de "a pasta …" e "a versão …" nas mensagens ("do agente", "do Hub").</param>
    private sealed record PackageKind(string Product, string Name, string Dll, string Label, string[] RequiredFiles);

    private sealed class Options
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ToolException($"Argumento inesperado: {args[i]}.");
                }

                var name = args[i][2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options._values[name] = args[++i];
                }
                else
                {
                    options._flags.Add(name);
                }
            }

            return options;
        }

        public string Required(string name) => Optional(name) ?? throw new ToolException($"Falta --{name}.");

        public string? Optional(string name) => _values.GetValueOrDefault(name);

        public bool Flag(string name) => _flags.Contains(name);
    }
}

internal sealed class ToolException(string message) : Exception(message);
