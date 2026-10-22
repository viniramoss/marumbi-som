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

    private readonly IAudioEndpoints _endpoints;
    private readonly IBluetoothConnection _connection;
    private readonly TimeProvider _time;
    private readonly ILogger<AudioEndpointManager> _logger;
    private readonly SemaphoreSlim _wake = new(0);

    private string? _lastNonSpeakerDefault;
    private bool _policyConfigDisabled;

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

        if (current != speakerEndpointId)
        {
            _lastNonSpeakerDefault = current;
            return;
        }

        var target = _lastNonSpeakerDefault ?? FindAlternativeOutput(speakerEndpointId);
        if (target is null)
        {
            return;
        }

        try
        {
            _endpoints.SetDefaultRenderEndpoint(target);
            _logger.LogInformation("Saída padrão do Windows restaurada para {EndpointId} (o Windows tinha trocado para a caixa)", target);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            // Seção 9.5: se a interface não documentada falhar nesta versão do Windows, registrar e seguir sem a função.
            _policyConfigDisabled = true;
            _logger.LogWarning(ex, "IPolicyConfig indisponível nesta versão do Windows; a saída padrão não será restaurada");
        }
    }

    private string? FindAlternativeOutput(string speakerEndpointId)
    {
        var speakerContainer = _endpoints.ListRenderEndpoints().FirstOrDefault(e => e.Id == speakerEndpointId)?.ContainerId;
        return _endpoints.ListRenderEndpoints()
            .FirstOrDefault(e => e.State == AudioEndpointState.Active && e.Id != speakerEndpointId && e.ContainerId != speakerContainer)?.Id;
    }
}
