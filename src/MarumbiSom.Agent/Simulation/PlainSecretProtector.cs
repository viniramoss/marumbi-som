using System.Security.Cryptography;
using MarumbiSom.Agent.Windows.Abstractions;

namespace MarumbiSom.Agent.Simulation;

/// <summary>Só para o modo simulado e testes (fora do Windows não há DPAPI): guarda o segredo como está.</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public const string Prefix = "plain:";

    public string Protect(string secret) => Prefix + secret;

    public string Unprotect(string protectedSecret) =>
        protectedSecret is not null && protectedSecret.StartsWith(Prefix, StringComparison.Ordinal)
            ? protectedSecret[Prefix.Length..]
            : throw new CryptographicException("Segredo em formato desconhecido.");
}
