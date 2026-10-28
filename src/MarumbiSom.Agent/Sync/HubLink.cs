using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using MarumbiSom.Agent.LocalConfig;
using MarumbiSom.Agent.Windows.Abstractions;

namespace MarumbiSom.Agent.Sync;

/// <summary>Loja vinculada: endereço do Hub, identificação e token (já aberto).</summary>
public sealed record HubLinkInfo(Uri Url, string StoreId, string? StoreName, string Token);

public sealed class HubEnrollException(string message) : Exception(message);

/// <summary>
/// Vinculação ao Hub (seção 8.5). Guarda em config.json o endereço, a loja e o token protegido por DPAPI; avisa
/// o <see cref="HubClient"/> e o <see cref="SyncEngine"/> quando muda.
/// </summary>
public sealed class HubLink(LocalConfigFile config, ISecretProtector protector, IHubTransport transport, ILogger<HubLink> logger)
{
    public const string Section = "Hub";
    private const string UrlKey = "Url";
    private const string StoreIdKey = "StoreId";
    private const string StoreNameKey = "StoreName";
    private const string TokenKey = "Token";
    private readonly Lock _lock = new();
    private bool _loaded;
    private HubLinkInfo? _current;

    public event EventHandler? Changed;

    public static string AgentVersion { get; } = ShortVersion(
        typeof(HubLink).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    public HubLinkInfo? Current
    {
        get
        {
            lock (_lock)
            {
                if (!_loaded)
                {
                    _current = Load();
                    _loaded = true;
                }

                return _current;
            }
        }
    }

    /// <summary>Aceita https (Tailscale Serve) ou http só para o próprio computador (testes).</summary>
    public static Uri? ParseUrl(string? text, out string? error)
    {
        error = null;
        var trimmed = text?.Trim() ?? string.Empty;
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Endereço do Hub inválido. Exemplo: https://hub-escritorio.exemplo.ts.net";
            return null;
        }

        var loopback = uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip));
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback))
        {
            error = "Use o endereço https do Hub (Tailscale Serve).";
            return null;
        }

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    /// <summary>Troca o código de vinculação pelo token (<c>POST /agent/enroll</c>) e grava tudo.</summary>
    public async Task<HubLinkInfo> EnrollAsync(string url, string code, CancellationToken cancellationToken)
    {
        var baseUri = ParseUrl(url, out var error) ?? throw new HubEnrollException(error!);
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new HubEnrollException("Informe o código de vinculação gerado no painel.");
        }

        EnrollResponse? enrolled;
        try
        {
            using var client = transport.CreateClient(baseUri, TimeSpan.FromSeconds(30));
            using var response = await client.PostAsJsonAsync("agent/enroll", new { code = code.Trim() }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var problem = await ReadErrorAsync(response, cancellationToken);
                throw new HubEnrollException(problem ?? $"O Hub recusou a vinculação ({(int)response.StatusCode}).");
            }

            enrolled = await response.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HubEnrollException($"Não foi possível falar com o Hub em {baseUri}: {ex.Message}");
        }

        if (enrolled is null || string.IsNullOrEmpty(enrolled.Token) || string.IsNullOrEmpty(enrolled.StoreId))
        {
            throw new HubEnrollException("Resposta inválida do Hub.");
        }

        var info = new HubLinkInfo(baseUri, enrolled.StoreId, enrolled.StoreName, enrolled.Token);
        lock (_lock)
        {
            config.Set(Section, UrlKey, baseUri.ToString());
            config.Set(Section, StoreIdKey, enrolled.StoreId);
            config.Set(Section, StoreNameKey, enrolled.StoreName);
            config.Set(Section, TokenKey, protector.Protect(enrolled.Token));
            _current = info;
            _loaded = true;
        }

        logger.LogInformation("Loja vinculada ao Hub {Url} como {Store}", baseUri, enrolled.StoreId);
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    public void Unlink()
    {
        lock (_lock)
        {
            foreach (var key in new[] { UrlKey, StoreIdKey, StoreNameKey, TokenKey })
            {
                config.Set(Section, key, null);
            }

            _current = null;
            _loaded = true;
        }

        logger.LogInformation("Loja desvinculada do Hub; segue tocando a última configuração");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private HubLinkInfo? Load()
    {
        var url = config.Get(Section, UrlKey);
        var storeId = config.Get(Section, StoreIdKey);
        var token = config.Get(Section, TokenKey);
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(storeId) || string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var uri = ParseUrl(url, out var error) ?? throw new FormatException(error);
            return new HubLinkInfo(uri, storeId, config.Get(Section, StoreNameKey), protector.Unprotect(token));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Ex.: config.json copiado de outro PC. Sem token não sincroniza, mas a música continua.
            logger.LogError(ex, "Não foi possível ler a vinculação com o Hub em config.json; vincule a loja de novo");
            return null;
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(cancellationToken);
            return body?.Error;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ShortVersion(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 && version.Length > plus + 8 ? version[..(plus + 8)] : version;
    }

    private sealed record EnrollResponse(string StoreId, string? StoreName, string Token);

    private sealed record ErrorBody(string? Error);
}
