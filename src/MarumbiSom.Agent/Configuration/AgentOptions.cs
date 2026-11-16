namespace MarumbiSom.Agent.Configuration;

public sealed class BluetoothOptions
{
    public const string Section = "Bluetooth";

    /// <summary>Endereço (MAC) da caixa escolhida, ex.: "AA:BB:CC:DD:EE:FF" (seção 9.4).</summary>
    public string? SpeakerAddress { get; set; }

    /// <summary>InstanceId do adaptador USB. Vazio = detecta automaticamente.</summary>
    public string? AdapterInstanceId { get; set; }

    /// <summary>Espera entre tentativas de conexão (seção 9.1). Padrão 5, 10, 20, 40, 60 s.</summary>
    public IReadOnlyList<int>? BackoffSeconds { get; set; }

    /// <summary>Tempo entre aplicar um método de reconexão e conferir se o endpoint ficou ativo.</summary>
    public double VerifyDelaySeconds { get; set; } = 4;

    /// <summary>Intervalo de verificação enquanto conectado ou aguardando.</summary>
    public double PollSeconds { get; set; } = 2;

    /// <summary>Nível 3 (reiniciar adaptador) no máximo 1 vez a cada N minutos (seção 9.2).</summary>
    public int AdapterResetMinIntervalMinutes { get; set; } = 10;

    /// <summary>
    /// D41: depois de N tentativas seguidas sem a caixa (ex.: loja fechada, caixa desligada), o guardião entra no modo
    /// calmo: tenta a cada <see cref="QuietRetrySeconds"/>, quase sempre só com o método A, para não ficar reiniciando o
    /// Bluetooth do Windows a noite inteira. 0 desliga o modo calmo.
    /// </summary>
    public int QuietAfterFailedAttempts { get; set; } = 15;

    /// <summary>D41: espera entre tentativas no modo calmo.</summary>
    public int QuietRetrySeconds { get; set; } = 300;

    /// <summary>D41: no modo calmo, o adaptador (método C e nível 3) é reiniciado no máximo 1 vez a cada N minutos.</summary>
    public int QuietAdapterResetMinIntervalMinutes { get; set; } = 60;

    public IReadOnlyList<TimeSpan> EffectiveBackoff =>
        (BackoffSeconds is { Count: > 0 } ? BackoffSeconds : [5, 10, 20, 40, 60]).Select(s => TimeSpan.FromSeconds(s)).ToArray();
}

public sealed class PlayerOptions
{
    public const string Section = "Player";

    /// <summary>Caminho do mpv.exe. Relativo = relativo à pasta do agente.</summary>
    public string MpvPath { get; set; } = Path.Combine("tools", "mpv.exe");

    public string MusicAPipeName { get; set; } = "marumbisom-mpv-a";

    public string MusicBPipeName { get; set; } = "marumbisom-mpv-b";

    public string SpotPipeName { get; set; } = "marumbisom-mpv-spot";

    public string ResolveMpvPath() => Path.IsPathRooted(MpvPath) ? MpvPath : Path.Combine(AppContext.BaseDirectory, MpvPath);
}

public static class AgentPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MarumbiSom");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
}
