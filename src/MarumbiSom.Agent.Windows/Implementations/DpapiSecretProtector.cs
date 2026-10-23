using System.Security.Cryptography;
using System.Text;
using MarumbiSom.Agent.Windows.Abstractions;

namespace MarumbiSom.Agent.Windows.Implementations;

/// <summary>
/// DPAPI com escopo de máquina (seção 8.2): só processos deste PC abrem o segredo. Junto com a ACL de
/// C:\ProgramData\MarumbiSom (SYSTEM e Administradores), o funcionário não lê o token.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "MarumbiSom.Agent.HubToken"u8.ToArray();

    public string Protect(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(data);
    }

    public string Unprotect(string protectedSecret)
    {
        ArgumentNullException.ThrowIfNull(protectedSecret);
        if (!protectedSecret.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Segredo não está protegido com DPAPI.");
        }

        var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret[Prefix.Length..]), Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(data);
    }
}
