namespace MarumbiSom.Hub;

public sealed class HubOptions
{
    public const string Section = "Hub";

    /// <summary>Pasta de dados. Vazio = <see cref="HubPaths.DefaultDataDirectory"/>.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Porta em 127.0.0.1 (seção 6.1); o acesso externo é só pelo Tailscale Serve.</summary>
    public int Port { get; set; } = 5080;

    /// <summary>Caminho do ffmpeg; relativo à pasta do exe. Se não existir, usa o do PATH.</summary>
    public string FfmpegPath { get; set; } = OperatingSystem.IsWindows() ? @"tools\ffmpeg.exe" : "tools/ffmpeg";

    public string FfprobePath { get; set; } = OperatingSystem.IsWindows() ? @"tools\ffprobe.exe" : "tools/ffprobe";

    /// <summary>Hora local da regeneração diária dos manifestos (seção 6.4).</summary>
    public TimeOnly DailyRegenerationTime { get; set; } = new(0, 5);

    /// <summary>Fuso do PC do escritório para a regeneração diária.</summary>
    public string Timezone { get; set; } = "America/Sao_Paulo";

    public static string ResolveTool(string configured, string fallbackName)
    {
        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
        return File.Exists(path) ? path : fallbackName;
    }
}
