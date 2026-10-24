using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MarumbiSom.Contracts.Releases;

/// <summary>
/// Assinatura Ed25519 dos pacotes do agente (seção 12.2, D27 a). A mensagem assinada amarra a versão ao SHA-256 do
/// .zip, então um pacote antigo não passa por um novo. Chaves e assinaturas circulam em Base64.
/// </summary>
public static partial class ReleaseSigning
{
    public const string PublicKeyFileName = "release-public-key.txt";

    private const string PrivateKeyPrefix = "marumbi-som-ed25519-private:";

    public static byte[] Message(string version, string sha256) =>
        Encoding.UTF8.GetBytes($"marumbi-som-agent|{version}|{sha256.ToLowerInvariant()}");

    /// <summary>Par novo: a privada no formato do arquivo de chave privada, a pública em Base64.</summary>
    public static (string PrivateKeyFile, string PublicKey) NewKeyPair()
    {
        var seed = RandomNumberGenerator.GetBytes(Ed25519PrivateKeyParameters.KeySize);
        var key = new Ed25519PrivateKeyParameters(seed);
        return (PrivateKeyPrefix + Convert.ToBase64String(seed), Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
    }

    public static string PublicKeyOf(string privateKeyFile) =>
        Convert.ToBase64String(ParsePrivate(privateKeyFile).GeneratePublicKey().GetEncoded());

    public static string Sign(string privateKeyFile, string version, string sha256)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, ParsePrivate(privateKeyFile));
        var message = Message(version, sha256);
        signer.BlockUpdate(message, 0, message.Length);
        return Convert.ToBase64String(signer.GenerateSignature());
    }

    public static bool Verify(string publicKey, string version, string sha256, string signature)
    {
        byte[] keyBytes;
        byte[] signatureBytes;
        try
        {
            keyBytes = Convert.FromBase64String(publicKey.Trim());
            signatureBytes = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (keyBytes.Length != Ed25519PublicKeyParameters.KeySize || signatureBytes.Length != Ed25519PrivateKeyParameters.SignatureSize)
        {
            return false;
        }

        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(keyBytes));
        var message = Message(version, sha256);
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signatureBytes);
    }

    /// <summary>Lê o <c>release-public-key.txt</c>: a primeira linha que não é comentário (<c>#</c>); nulo se não houver.</summary>
    public static string? ReadPublicKey(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));

    /// <summary>Versão de pacote: três números, como o CI gera (0.6.41).</summary>
    public static bool IsVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    /// <summary>"0.6.41+36672a7" → "0.6.41".</summary>
    public static string ShortVersion(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    /// <summary>Compara versões numéricas (0.6.9 &lt; 0.6.10); versões fora do padrão contam como menores.</summary>
    public static int CompareVersions(string a, string b) =>
        Comparer<(long, long, long)>.Default.Compare(Parts(a), Parts(b));

    private static (long, long, long) Parts(string version)
    {
        if (!IsVersion(version))
        {
            return (-1, -1, -1);
        }

        var parts = version.Split('.').Select(p => long.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return (parts[0], parts[1], parts[2]);
    }

    private static Ed25519PrivateKeyParameters ParsePrivate(string privateKeyFile)
    {
        var line = ReadPublicKey(privateKeyFile ?? string.Empty);
        if (line is null || !line.StartsWith(PrivateKeyPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("Arquivo de chave privada inválido (esperado o formato do new-signing-key).");
        }

        var seed = Convert.FromBase64String(line[PrivateKeyPrefix.Length..]);
        if (seed.Length != Ed25519PrivateKeyParameters.KeySize)
        {
            throw new FormatException("Chave privada com tamanho errado.");
        }

        return new Ed25519PrivateKeyParameters(seed);
    }

    [GeneratedRegex(@"^\d{1,5}\.\d{1,5}\.\d{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

/// <summary>Conteúdo do <c>.sig.json</c> que acompanha o .zip do agente (D27 b).</summary>
public sealed record ReleaseSignature(string Version, string Sha256, string Signature);

/// <summary>Versão do agente como o Hub entrega no comando <c>updateAgent</c> e em <c>/agent/releases/current</c> (D27 d).</summary>
public sealed record AgentReleaseInfo(string Version, string Sha256, long SizeBytes, string Signature);

/// <summary>Andamento de uma atualização, no campo <c>update</c> do status (D27 e). <see cref="Stage"/> é de <see cref="UpdateStages"/>.</summary>
public sealed record UpdateProgress(string Version, string Stage, long DoneBytes, long TotalBytes, string? Error = null);

public static class UpdateStages
{
    public const string Downloading = "downloading";
    public const string Verifying = "verifying";
    public const string Installing = "installing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string RolledBack = "rolledBack";

    public static bool InProgress(string stage) => stage is Downloading or Verifying or Installing;
}
