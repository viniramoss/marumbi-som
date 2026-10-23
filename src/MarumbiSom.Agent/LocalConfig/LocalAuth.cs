using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MarumbiSom.Agent.LocalConfig;

public enum LoginOutcome
{
    Success,
    WrongPassword,
    LockedOut,
    NotConfigured,
}

public sealed record LoginResult(LoginOutcome Outcome, string? SessionToken = null, int RemainingAttempts = 0, DateTimeOffset? LockedUntil = null);

/// <summary>
/// Login da configuração local: 5 senhas erradas bloqueiam por 15 min (seção 8.4). Sessões ficam só em
/// memória e expiram após 30 min sem uso.
/// </summary>
public sealed class LocalAuth(LocalConfigFile config, TimeProvider time, ILogger<LocalAuth> logger)
{
    public const string Section = "LocalConfig";
    public const string PasswordKey = "PasswordHash";
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SessionIdle = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private int _failures;
    private DateTimeOffset? _lockedUntil;

    public bool PasswordConfigured => !string.IsNullOrEmpty(config.Get(Section, PasswordKey));

    public DateTimeOffset? LockedUntil
    {
        get
        {
            lock (_lock)
            {
                return _lockedUntil is { } until && until > time.GetUtcNow() ? until : null;
            }
        }
    }

    public LoginResult Login(string? password)
    {
        var result = CheckPassword(password);
        if (result.Outcome != LoginOutcome.Success)
        {
            return result;
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = time.GetUtcNow() + SessionIdle;
        logger.LogInformation("Login na configuração local");
        return result with { SessionToken = token };
    }

    /// <summary>Confere a senha contando tentativas erradas para o bloqueio, sem abrir sessão.</summary>
    public LoginResult CheckPassword(string? password)
    {
        var stored = config.Get(Section, PasswordKey);
        if (string.IsNullOrEmpty(stored))
        {
            return new LoginResult(LoginOutcome.NotConfigured);
        }

        lock (_lock)
        {
            var now = time.GetUtcNow();
            if (_lockedUntil is { } until && until > now)
            {
                return new LoginResult(LoginOutcome.LockedOut, LockedUntil: until);
            }

            if (LocalPassword.Verify(password ?? string.Empty, stored))
            {
                _failures = 0;
                _lockedUntil = null;
                return new LoginResult(LoginOutcome.Success);
            }

            _failures++;
            if (_failures >= MaxAttempts)
            {
                _failures = 0;
                _lockedUntil = now + Lockout;
                logger.LogWarning("Configuração local bloqueada até {Until} após {Max} senhas erradas", _lockedUntil, MaxAttempts);
                return new LoginResult(LoginOutcome.LockedOut, LockedUntil: _lockedUntil);
            }

            logger.LogWarning("Senha errada na configuração local ({Failures}/{Max})", _failures, MaxAttempts);
            return new LoginResult(LoginOutcome.WrongPassword, RemainingAttempts: MaxAttempts - _failures);
        }
    }

    public bool Validate(string? token)
    {
        if (token is null || !_sessions.TryGetValue(token, out var expires))
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (expires <= now)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        _sessions[token] = now + SessionIdle;
        return true;
    }

    public void Logout(string? token)
    {
        if (token is not null)
        {
            _sessions.TryRemove(token, out _);
        }
    }

    /// <summary>Troca a senha. Encerra todas as sessões abertas.</summary>
    public void SetPassword(string password)
    {
        config.Set(Section, PasswordKey, LocalPassword.Hash(password));
        _sessions.Clear();
        logger.LogInformation("Senha da configuração local alterada");
    }
}
