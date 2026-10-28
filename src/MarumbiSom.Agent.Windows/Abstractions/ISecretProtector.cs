namespace MarumbiSom.Agent.Windows.Abstractions;

/// <summary>Protege segredos gravados em config.json (token do Hub, seção 8.2).</summary>
public interface ISecretProtector
{
    /// <summary>Texto protegido, com prefixo que identifica o método (ex.: <c>dpapi:</c>).</summary>
    string Protect(string secret);

    /// <summary>Devolve o segredo; lança <see cref="System.Security.Cryptography.CryptographicException"/> se não der para abrir.</summary>
    string Unprotect(string protectedSecret);
}
