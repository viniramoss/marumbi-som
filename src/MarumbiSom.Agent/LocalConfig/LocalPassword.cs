using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MarumbiSom.Agent.LocalConfig;

/// <summary>Senha da configuração local guardada como hash PBKDF2-SHA256 (seção 8.4). Formato: pbkdf2-sha256$iterações$sal$hash.</summary>
public static class LocalPassword
{
    public const int MinLength = 8;
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, Iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || stored?.Split('$') is not [Scheme, var iterationsText, var saltText, var hashText]
            || !int.TryParse(iterationsText, NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 10_000)
        {
            return false;
        }

        try
        {
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(saltText), iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Motivo pelo qual a nova senha não é aceita, ou nulo.</summary>
    public static string? Validate(string? password) =>
        string.IsNullOrWhiteSpace(password) || password.Length < MinLength ? $"A senha precisa ter pelo menos {MinLength} caracteres." : null;
}
