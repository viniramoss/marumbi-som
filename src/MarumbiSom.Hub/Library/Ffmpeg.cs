using System.Diagnostics;
using System.Text;

namespace MarumbiSom.Hub.Library;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Executa ffmpeg/ffprobe com argumentos separados (sem shell) e tempo limite.</summary>
public sealed class Ffmpeg(string ffmpegPath, string ffprobePath)
{
    public static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(15);

    public string FfmpegPath { get; } = ffmpegPath;

    public string FfprobePath { get; } = ffprobePath;

    public Task<ProcessResult> RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(FfmpegPath, ["-hide_banner", "-nostdin", "-y", .. arguments], cancellationToken);

    public Task<ProcessResult> RunFfprobeAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(FfprobePath, ["-v", "error", .. arguments], cancellationToken);

    private static async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Não foi possível iniciar {executable}.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StepTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
