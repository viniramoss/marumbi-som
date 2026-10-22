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

    public IReadOnlyList<TimeSpan> EffectiveBackoff =>
        (BackoffSeconds is { Count: > 0 } ? BackoffSeconds : [5, 10, 20, 40, 60]).Select(s => TimeSpan.FromSeconds(s)).ToArray();
}

public sealed class PlayerOptions
{
    public const string Section = "Player";

    /// <summary>Caminho do mpv.exe. Relativo = relativo à pasta do agente.</summary>
    public string MpvPath { get; set; } = Path.Combine("tools", "mpv.exe");

    /// <summary>Volume da loja (0–100), aplicado no mpv. O endpoint da caixa fica fixo em 100%.</summary>
    public int Volume { get; set; } = 70;

    public string MusicPipeName { get; set; } = "marumbisom-mpv";

    public string SpotPipeName { get; set; } = "marumbisom-mpv-spot";

    public string ResolveMpvPath() => Path.IsPathRooted(MpvPath) ? MpvPath : Path.Combine(AppContext.BaseDirectory, MpvPath);
}

/// <summary>Configuração da prova de conceito da Etapa 1 (reprodução em loop de uma pasta local).</summary>
public sealed class PocOptions
{
    public const string Section = "Poc";

    public bool Enabled { get; set; } = true;

    public string MusicFolder { get; set; } = Path.Combine(AgentPaths.DataDirectory, "poc", "musicas");

    /// <summary>Áudio de oferta tocado periodicamente com ducking, para validar a mistura de dois mpv.</summary>
    public string? SpotFile { get; set; } = Path.Combine(AgentPaths.DataDirectory, "poc", "oferta.m4a");

    public double SpotIntervalMinutes { get; set; } = 2;

    public int DuckLevel { get; set; } = 30;
}

public static class AgentPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MarumbiSom");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");
}
