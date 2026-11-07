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

    /// <summary>Repositório (dono/nome) onde o GitHub Actions publica as versões do agente (D29). Vazio desliga a procura.</summary>
    public string? GitHubRepository { get; set; } = "viniramoss/marumbi-som";

    /// <summary>De quantas em quantas horas o Hub procura versão nova no GitHub (1 a 168).</summary>
    public int ReleaseCheckHours { get; set; } = 6;

    /// <summary>Hora local (fuso <see cref="Timezone"/>) em que o Hub instala sozinho a versão nova dele (D32 f).</summary>
    public TimeOnly HubUpdateTime { get; set; } = new(3, 0);

    /// <summary>
    /// Chave pública que o Hub usa para conferir o pacote do Hub antes de pedir a troca. Vazio = o
    /// <c>release-public-key.txt</c> ao lado do exe. Só os testes trocam: quem instala confere de novo com a chave de
    /// <c>app\</c> (D32 d).
    /// </summary>
    public string? UpdatePublicKeyFile { get; set; }

    public static string ResolveTool(string configured, string fallbackName)
    {
        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
        return File.Exists(path) ? path : fallbackName;
    }
}
