using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Windows.Abstractions;

namespace MarumbiSom.Agent.Audio;

/// <summary>
/// Impõe o volume do endpoint da caixa em 100% e devolve a saída padrão do Windows ao alto-falante do PC
/// quando o Windows a muda para a caixa (seção 9.5). Reaplica a cada 10 s e logo após a caixa conectar.
/// </summary>
public sealed class AudioEndpointManager : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private const int MaxBounces = 3;

    private readonly IAudioEndpoints _endpoints;
    private readonly IBluetoothConnection _connection;
    private readonly TimeProvider _time;
    private readonly ILogger<AudioEndpointManager> _logger;
    private readonly SemaphoreSlim _wake = new(0);

    private string? _lastNonSpeakerDefault;
    private bool _policyConfigDisabled;
    private bool _restoredLastPass;
    private int _bounces;
    private volatile bool _gaveUp;

    public AudioEndpointManager(IAudioEndpoints endpoints, IBluetoothConnection connection, TimeProvider time, ILogger<AudioEndpointManager> logger)
    {
        _endpoints = endpoints;
        _connection = connection;
        _time = time;
        _logger = logger;
        _connection.ConnectionChanged += (_, e) =>
        {
            if (e.IsConnected)
            {
                _gaveUp = false;
                _wake.Release();
            }
        };
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    /// <summary>Uma passada de imposição. Pública para testes.</summary>
    public void Enforce()
    {
        var speaker = _connection.IsConnected ? _connection.SpeakerEndpointId : null;
        EnforceDefaultOutput(speaker);
        if (speaker is null)
        {
            return;
        }

        try
        {
            _endpoints.SetEndpointVolume(speaker, 1.0f);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _logger.LogWarning(ex, "Falha ao fixar o volume do endpoint da caixa em 100%");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RememberCurrentDefault();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Enforce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha no AudioEndpointManager");
            }

            // Acorda antes se a caixa acabou de conectar; espera 1,5 s para o Windows terminar de trocar a saída padrão.
            if (await _wake.WaitAsync(Interval, stoppingToken))
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), _time, stoppingToken);
            }
        }
    }

    private void RememberCurrentDefault()
    {
        try
        {
            var current = _endpoints.GetDefaultRenderEndpointId();
            if (current is not null && current != _connection.SpeakerEndpointId)
            {
                _lastNonSpeakerDefault = current;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            _logger.LogWarning(ex, "Falha ao ler a saída padrão do Windows");
        }
    }

    private void EnforceDefaultOutput(string? speakerEndpointId)
    {
        if (_policyConfigDisabled)
        {
            return;
        }

        var current = _endpoints.GetDefaultRenderEndpointId();
        if (current is null)
        {
            return;
        }

        var endpoints = _endpoints.ListRenderEndpoints();
        if (!IsSpeakerSide(current, speakerEndpointId, endpoints))
        {
            _lastNonSpeakerDefault = current;
            _bounces = 0;
            _restoredLastPass = false;
            return;
        }

        if (_gaveUp)
        {
            return;
        }

        // O Windows devolveu a saída para a caixa logo depois da última restauração (PC piloto, 23/10/2026: a cada 10 s).
        // Brigar com ele não adianta e cada troca pode cortar o áudio da caixa; desiste até a caixa reconectar.
        if (_restoredLastPass && ++_bounces >= MaxBounces)
        {
            _logger.LogWarning(
                "O Windows voltou a saída padrão para a caixa {Count} vezes seguidas; a saída padrão fica na caixa até ela reconectar",
                _bounces);
            _gaveUp = true;
            _bounces = 0;
            _restoredLastPass = false;
            return;
        }

        // Só restaura para uma saída ativa que não seja da própria caixa (o perfil "Viva-voz" da caixa tem outro endpoint no
        // mesmo contêiner; usá-lo como padrão abre o perfil de chamada e corta o áudio estéreo).
        var target = Usable(_lastNonSpeakerDefault, speakerEndpointId, endpoints)
            ?? endpoints.FirstOrDefault(e => e.State == AudioEndpointState.Active && !IsSpeakerSide(e.Id, speakerEndpointId, endpoints));
        if (target is null)
        {
            return;
        }

        try
        {
            _endpoints.SetDefaultRenderEndpoint(target.Id);
            _restoredLastPass = true;
            _logger.LogInformation(
                "Saída padrão do Windows restaurada para {Target} ({EndpointId}); o Windows tinha trocado para {Current}",
                target.FriendlyName, target.Id, NameOf(current, endpoints));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            // Seção 9.5: se a interface não documentada falhar nesta versão do Windows, registrar e seguir sem a função.
            _policyConfigDisabled = true;
            _logger.LogWarning(ex, "IPolicyConfig indisponível nesta versão do Windows; a saída padrão não será restaurada");
        }
    }

    /// <summary>O endpoint é a caixa ou outro endpoint do mesmo dispositivo (mesmo contêiner, ex.: "Viva-voz").</summary>
    private static bool IsSpeakerSide(string endpointId, string? speakerEndpointId, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (speakerEndpointId is null)
        {
            return false;
        }

        if (endpointId == speakerEndpointId)
        {
            return true;
        }

        var container = endpoints.FirstOrDefault(e => e.Id == speakerEndpointId)?.ContainerId;
        return container is not null && endpoints.Any(e => e.Id == endpointId && e.ContainerId == container);
    }

    private static AudioEndpoint? Usable(string? endpointId, string? speakerEndpointId, IReadOnlyList<AudioEndpoint> endpoints) =>
        endpointId is null || IsSpeakerSide(endpointId, speakerEndpointId, endpoints)
            ? null
            : endpoints.FirstOrDefault(e => e.Id == endpointId && e.State == AudioEndpointState.Active);

    private static string NameOf(string endpointId, IReadOnlyList<AudioEndpoint> endpoints) =>
        endpoints.FirstOrDefault(e => e.Id == endpointId)?.FriendlyName ?? endpointId;
}
