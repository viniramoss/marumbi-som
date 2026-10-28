using MarumbiSom.Agent.Configuration;
using MarumbiSom.Agent.LocalConfig;
using MarumbiSom.Agent.Windows.Abstractions;
using MarumbiSom.Contracts.Messages;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Agent.Bluetooth;

public enum GuardianState
{
    Initializing,
    CheckingRadio,
    TurningOnRadio,
    Connecting,
    Verifying,
    Connected,
    Waiting,

    /// <summary>Comando "reiniciar áudio" (D37): reinicia o áudio e o Bluetooth do Windows antes de reconectar.</summary>
    RestartingAudio,
}

public enum ReconnectMethod
{
    /// <summary>KSPROPERTY_ONESHOT_RECONNECT no endpoint.</summary>
    A,

    /// <summary>Desligar/religar o serviço A2DP Sink.</summary>
    B,

    /// <summary>Reiniciar o adaptador (nível 3) e voltar ao método A.</summary>
    C,
}

/// <summary>
/// Guardião Bluetooth (seção 9): mantém o rádio ligado e a caixa conectada.
/// A lógica é uma máquina de estados avançada por <see cref="StepAsync"/>, que devolve quanto esperar
/// até o próximo passo; isso permite testá-la sem relógio real.
/// </summary>
public sealed class BluetoothGuardian : IBluetoothConnection, ISpeakerSelection
{
    /// <summary>Espera entre entrar em <see cref="GuardianState.RestartingAudio"/> e reiniciar os serviços (D37 c).</summary>
    public static readonly TimeSpan AudioRestartGrace = TimeSpan.FromSeconds(2);

    /// <summary>Depois do reinício, o status segue "reiniciando o áudio" até a caixa voltar ou esse tempo passar (D37 d).</summary>
    public static readonly TimeSpan AudioRestartReconnectWindow = TimeSpan.FromSeconds(90);

    private readonly IBluetoothRadio _radio;
    private readonly ISystemServices _services;
    private readonly IBluetoothAdapterControl _adapter;
    private readonly IBluetoothSpeakerControl _speaker;
    private readonly IAudioEndpoints _endpoints;
    private readonly BluetoothOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BluetoothGuardian> _logger;
    private readonly IReadOnlyList<TimeSpan> _backoff;
    private BluetoothAddress? _speakerAddress;
    private volatile SpeakerChange? _pendingSpeaker;

    private Guid? _containerId;
    private int _radioLevel;
    private bool _softwareRetryPending;
    private int _failedAttempts;
    private DateTimeOffset? _lastAdapterReset;
    private DateTimeOffset _waitUntil;
    private volatile bool _reconnectRequested;
    private volatile bool _audioRestartRequested;

    // UtcTicks do fim da janela da D37 d (0 = fora dela); lido pelo status em outra thread.
    private long _audioRestartUntilTicks;

    public BluetoothGuardian(
        IBluetoothRadio radio,
        ISystemServices services,
        IBluetoothAdapterControl adapter,
        IBluetoothSpeakerControl speaker,
        IAudioEndpoints endpoints,
        IOptions<BluetoothOptions> options,
        TimeProvider time,
        ILogger<BluetoothGuardian> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _radio = radio;
        _services = services;
        _adapter = adapter;
        _speaker = speaker;
        _endpoints = endpoints;
        _options = options.Value;
        _time = time;
        _logger = logger;
        _backoff = _options.EffectiveBackoff;
        if (BluetoothAddress.TryParse(_options.SpeakerAddress, out var address))
        {
            _speakerAddress = address;
        }
        else
        {
            _logger.LogError("Bluetooth:SpeakerAddress ausente ou inválido ('{Address}'); o guardião não vai conectar nenhuma caixa", _options.SpeakerAddress);
        }
    }

    public event EventHandler<BluetoothConnectionChangedEventArgs>? ConnectionChanged;

    /// <summary>Disparado a cada ação de recuperação (nível do rádio ou método de conexão), para telemetria e testes.</summary>
    public event EventHandler<string>? RecoveryAction;

    public GuardianState State { get; private set; } = GuardianState.Initializing;

    public string? SpeakerEndpointId { get; private set; }

    public bool IsConnected => State == GuardianState.Connected;

    public int FailedAttempts => _failedAttempts;

    public BluetoothStatus Status => State switch
    {
        GuardianState.Connected => BluetoothStatus.Connected,
        GuardianState.RestartingAudio => BluetoothStatus.RestartingAudio,
        _ when _time.GetUtcNow().UtcTicks < Interlocked.Read(ref _audioRestartUntilTicks) => BluetoothStatus.RestartingAudio,
        GuardianState.TurningOnRadio => BluetoothStatus.RadioOff,
        GuardianState.Connecting or GuardianState.Verifying => BluetoothStatus.Connecting,
        _ => BluetoothStatus.Disconnected,
    };

    private TimeSpan Poll => TimeSpan.FromSeconds(_options.PollSeconds);

    public BluetoothAddress? SpeakerAddress => _pendingSpeaker is { } pending ? pending.Address : _speakerAddress;

    public void RequestReconnect() => _reconnectRequested = true;

    public void RequestAudioRestart() => _audioRestartRequested = true;

    /// <summary>Troca a caixa (configuração local, seção 8.4). Vale a partir do próximo passo, sem reiniciar o serviço.</summary>
    public void SetSpeaker(BluetoothAddress? address) => _pendingSpeaker = new SpeakerChange(address);

    /// <summary>Executa um passo da máquina de estados e devolve a espera até o próximo.</summary>
    public async Task<TimeSpan> StepAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Interlocked.Read(ref _audioRestartUntilTicks) is > 0 and var until && _time.GetUtcNow().UtcTicks >= until)
            {
                Interlocked.Exchange(ref _audioRestartUntilTicks, 0);
                _logger.LogWarning("A caixa não voltou em {Window} depois do reinício do áudio; o guardião segue tentando", AudioRestartReconnectWindow);
            }

            if (_pendingSpeaker is { } change)
            {
                _pendingSpeaker = null;
                _logger.LogInformation("Caixa trocada para {Address}", change.Address?.ToString() ?? "(nenhuma)");
                _speakerAddress = change.Address;
                _containerId = null;
                _failedAttempts = 0;
                SetDisconnected();
                SpeakerEndpointId = null;
                if (State is not (GuardianState.Initializing or GuardianState.RestartingAudio))
                {
                    State = GuardianState.CheckingRadio;
                }
            }

            if (_audioRestartRequested)
            {
                _audioRestartRequested = false;
                _reconnectRequested = false;
                _logger.LogWarning("Reinício do áudio e do Bluetooth do Windows solicitado pelo administrador");
                Interlocked.Exchange(ref _audioRestartUntilTicks, 0);
                SetDisconnected();
                State = GuardianState.RestartingAudio;
                // Tempo para o player pausar e fechar os mpv antes de o Windows derrubar a saída de áudio.
                return AudioRestartGrace;
            }

            if (_reconnectRequested)
            {
                _reconnectRequested = false;
                _logger.LogInformation("Reconexão do Bluetooth solicitada pelo administrador");
                _failedAttempts = 0;
                // Durante o reinício do áudio a reconexão já vem no fim dele (D37 d).
                if (State is not (GuardianState.Initializing or GuardianState.RestartingAudio))
                {
                    SetDisconnected();
                    State = GuardianState.CheckingRadio;
                }
            }

            return State switch
            {
                GuardianState.Initializing => await InitializeAsync(cancellationToken),
                GuardianState.CheckingRadio => await CheckRadioAsync(cancellationToken),
                GuardianState.TurningOnRadio => await TurnOnRadioAsync(cancellationToken),
                GuardianState.Connecting => await ConnectAsync(cancellationToken),
                GuardianState.Verifying => await VerifyAsync(cancellationToken),
                GuardianState.Connected => await MonitorConnectedAsync(cancellationToken),
                GuardianState.Waiting => await WaitAsync(cancellationToken),
                GuardianState.RestartingAudio => await RestartAudioAsync(cancellationToken),
                _ => throw new InvalidOperationException($"Estado desconhecido {State}"),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha no guardião Bluetooth no estado {State}", State);
            return TimeSpan.FromSeconds(5);
        }
    }

    private async Task<TimeSpan> InitializeAsync(CancellationToken cancellationToken)
    {
        var allRunning = true;
        foreach (var name in new[] { WindowsServiceNames.Bluetooth, WindowsServiceNames.Audio })
        {
            if (_services.IsRunning(name))
            {
                continue;
            }

            allRunning = false;
            _logger.LogWarning("Serviço {Service} não está em execução; tentando iniciar", name);
            await _services.StartAsync(name, cancellationToken);
        }

        if (!allRunning)
        {
            return TimeSpan.FromSeconds(3);
        }

        _logger.LogInformation("Serviços bthserv e Audiosrv ativos");
        State = GuardianState.CheckingRadio;
        return TimeSpan.Zero;
    }

    private async Task<TimeSpan> CheckRadioAsync(CancellationToken cancellationToken)
    {
        var radio = await _radio.GetStateAsync(cancellationToken);
        if (radio == RadioPowerState.On)
        {
            if (_radioLevel > 0)
            {
                _logger.LogInformation("Rádio Bluetooth ligado (nível {Level})", _radioLevel);
            }

            _radioLevel = 0;
            _softwareRetryPending = false;
            State = GuardianState.Connecting;
            return TimeSpan.Zero;
        }

        if (_softwareRetryPending)
        {
            // Após reiniciar serviço ou adaptador o rádio volta no último estado salvo; se estava
            // desligado, tenta o nível 1 de novo antes de subir de nível.
            _softwareRetryPending = false;
            Report("radio.level1");
            await _radio.TurnOnAsync(cancellationToken);
            return TimeSpan.FromSeconds(1);
        }

        _radioLevel++;
        if (_radioLevel > 3)
        {
            _logger.LogError("Rádio Bluetooth continua {State} após os 3 níveis de recuperação", radio);
            _radioLevel = 0;
            return EnterWaiting();
        }

        _logger.LogWarning("Rádio Bluetooth {State}; aplicando nível {Level}", radio, _radioLevel);
        State = GuardianState.TurningOnRadio;
        return TimeSpan.Zero;
    }

    private async Task<TimeSpan> TurnOnRadioAsync(CancellationToken cancellationToken)
    {
        State = GuardianState.CheckingRadio;
        switch (_radioLevel)
        {
            case 1:
                Report("radio.level1");
                await _radio.TurnOnAsync(cancellationToken);
                return TimeSpan.FromSeconds(1);
            case 2:
                Report("radio.level2");
                await _services.RestartAsync(WindowsServiceNames.Bluetooth, cancellationToken);
                _softwareRetryPending = true;
                return TimeSpan.FromSeconds(3);
            default:
                if (await TryResetAdapterAsync(cancellationToken))
                {
                    _softwareRetryPending = true;
                    return TimeSpan.FromSeconds(5);
                }

                _radioLevel = 0;
                return EnterWaiting();
        }
    }

    private async Task<TimeSpan> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_speakerAddress is not { } address)
        {
            return EnterWaiting();
        }

        var endpoint = await FindSpeakerEndpointAsync(address, cancellationToken);
        if (endpoint?.State == AudioEndpointState.Active)
        {
            return SetConnected(endpoint);
        }

        if (await _radio.GetStateAsync(cancellationToken) != RadioPowerState.On)
        {
            State = GuardianState.CheckingRadio;
            return TimeSpan.Zero;
        }

        var method = (ReconnectMethod)(_failedAttempts % 3);
        _logger.LogInformation("Conectando a caixa {Address}: tentativa {Attempt}, método {Method}", address, _failedAttempts + 1, method);
        switch (method)
        {
            case ReconnectMethod.A when endpoint is not null:
                Report("connect.A");
                await _speaker.RequestReconnectAsync(endpoint.Id, cancellationToken);
                break;
            case ReconnectMethod.A:
            case ReconnectMethod.B:
                Report("connect.B");
                await _speaker.ToggleA2dpSinkAsync(address, cancellationToken);
                break;
            case ReconnectMethod.C:
                if (await TryResetAdapterAsync(cancellationToken))
                {
                    // Após reiniciar o adaptador, o próximo passo volta ao método A (seção 9.3).
                    State = GuardianState.Verifying;
                    return TimeSpan.FromSeconds(Math.Max(_options.VerifyDelaySeconds, 8));
                }

                if (endpoint is not null)
                {
                    Report("connect.A");
                    await _speaker.RequestReconnectAsync(endpoint.Id, cancellationToken);
                }

                break;
        }

        State = GuardianState.Verifying;
        return TimeSpan.FromSeconds(_options.VerifyDelaySeconds);
    }

    private async Task<TimeSpan> VerifyAsync(CancellationToken cancellationToken)
    {
        var endpoint = _speakerAddress is { } address ? await FindSpeakerEndpointAsync(address, cancellationToken) : null;
        if (endpoint?.State == AudioEndpointState.Active)
        {
            return SetConnected(endpoint);
        }

        _failedAttempts++;
        return EnterWaiting();
    }

    private async Task<TimeSpan> MonitorConnectedAsync(CancellationToken cancellationToken)
    {
        var endpoint = _speakerAddress is { } address ? await FindSpeakerEndpointAsync(address, cancellationToken) : null;
        if (endpoint?.State == AudioEndpointState.Active)
        {
            SpeakerEndpointId = endpoint.Id;
            return Poll;
        }

        _logger.LogWarning("Caixa desconectada (endpoint {State})", endpoint?.State.ToString() ?? "ausente");
        _failedAttempts = 0;
        SetDisconnected();
        State = GuardianState.Connecting;
        return TimeSpan.Zero;
    }

    private async Task<TimeSpan> WaitAsync(CancellationToken cancellationToken)
    {
        // Se a caixa voltar sozinha durante a espera, não é preciso esperar o backoff terminar.
        if (_speakerAddress is { } address && await FindSpeakerEndpointAsync(address, cancellationToken) is { State: AudioEndpointState.Active } endpoint)
        {
            return SetConnected(endpoint);
        }

        var remaining = _waitUntil - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            State = GuardianState.CheckingRadio;
            return TimeSpan.Zero;
        }

        return remaining < Poll ? remaining : Poll;
    }

    /// <summary>
    /// D37: reinicia o AudioEndpointBuilder (com o Audiosrv), o bthserv e o adaptador; uma etapa que falha não impede as
    /// seguintes. Depois volta para Inicializando, que religa o que ficou parado e reconecta a caixa.
    /// </summary>
    private async Task<TimeSpan> RestartAudioAsync(CancellationToken cancellationToken)
    {
        Report("audio.restart");
        var failed = new List<string>();
        foreach (var name in new[] { WindowsServiceNames.AudioEndpointBuilder, WindowsServiceNames.Bluetooth })
        {
            try
            {
                await _services.RestartAsync(name, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Falha ao reiniciar o serviço {Service}", name);
                failed.Add(name);
            }
        }

        // Pedido do administrador: não respeita o intervalo mínimo, mas conta para o limite do nível 3 automático.
        _lastAdapterReset = _time.GetUtcNow();
        Report("adapter.reset");
        try
        {
            if (!await _adapter.ResetAdapterAsync(cancellationToken))
            {
                failed.Add("adaptador");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Falha ao reiniciar o adaptador Bluetooth");
            failed.Add("adaptador");
        }

        if (failed.Count == 0)
        {
            _logger.LogInformation("Áudio e Bluetooth do Windows reiniciados; reconectando a caixa");
        }
        else
        {
            _logger.LogWarning("Reinício do áudio terminou com falha em {Failed}; reconectando a caixa mesmo assim", string.Join(", ", failed));
        }

        _containerId = null;
        _failedAttempts = 0;
        _radioLevel = 0;
        // O rádio volta no último estado salvo; se voltar desligado, o nível 1 é tentado de novo antes de escalar.
        _softwareRetryPending = true;
        SpeakerEndpointId = null;
        Interlocked.Exchange(ref _audioRestartUntilTicks, (_time.GetUtcNow() + AudioRestartReconnectWindow).UtcTicks);
        State = GuardianState.Initializing;
        return TimeSpan.FromSeconds(3);
    }

    private TimeSpan EnterWaiting()
    {
        var delay = _backoff[Math.Clamp(_failedAttempts - 1, 0, _backoff.Count - 1)];
        _waitUntil = _time.GetUtcNow() + delay;
        State = GuardianState.Waiting;
        // Sem caixa escolhida não há tentativa a registrar a cada 5 s; o erro já foi registrado na inicialização.
        _logger.Log(_speakerAddress is null ? LogLevel.Debug : LogLevel.Information, "Aguardando {Delay} antes da próxima tentativa", delay);
        return delay < Poll ? delay : Poll;
    }

    private async Task<bool> TryResetAdapterAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var minInterval = TimeSpan.FromMinutes(_options.AdapterResetMinIntervalMinutes);
        if (_lastAdapterReset is { } last && now - last < minInterval)
        {
            _logger.LogInformation("Reinício do adaptador ignorado: último há {Elapsed} (mínimo {Min})", now - last, minInterval);
            return false;
        }

        _lastAdapterReset = now;
        Report("adapter.reset");
        return await _adapter.ResetAdapterAsync(cancellationToken);
    }

    private async Task<AudioEndpoint?> FindSpeakerEndpointAsync(BluetoothAddress address, CancellationToken cancellationToken)
    {
        _containerId ??= await _speaker.GetContainerIdAsync(address, cancellationToken);
        if (_containerId is not { } container)
        {
            _logger.LogWarning("Caixa {Address} não encontrada entre os dispositivos pareados", address);
            return null;
        }

        var candidates = _endpoints.ListRenderEndpoints().Where(e => e.ContainerId == container).ToList();
        return candidates.FirstOrDefault(e => e.State == AudioEndpointState.Active) ?? candidates.FirstOrDefault();
    }

    private TimeSpan SetConnected(AudioEndpoint endpoint)
    {
        var wasConnected = IsConnected;
        Interlocked.Exchange(ref _audioRestartUntilTicks, 0);
        _failedAttempts = 0;
        _radioLevel = 0;
        SpeakerEndpointId = endpoint.Id;
        State = GuardianState.Connected;
        if (!wasConnected)
        {
            _logger.LogInformation("Caixa conectada: {Name} ({EndpointId})", endpoint.FriendlyName, endpoint.Id);
            ConnectionChanged?.Invoke(this, new BluetoothConnectionChangedEventArgs(true, endpoint.Id));
        }

        return Poll;
    }

    private void SetDisconnected()
    {
        if (State != GuardianState.Connected)
        {
            return;
        }

        State = GuardianState.Connecting;
        ConnectionChanged?.Invoke(this, new BluetoothConnectionChangedEventArgs(false, SpeakerEndpointId));
    }

    private void Report(string action) => RecoveryAction?.Invoke(this, action);

    private sealed record SpeakerChange(BluetoothAddress? Address);
}
