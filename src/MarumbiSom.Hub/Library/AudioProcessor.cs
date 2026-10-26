using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Hub.Library;

/// <summary>Arquivo enviado recusado pela validação; a mensagem é mostrada ao admin.</summary>
public sealed class IngestionRejectedException(string message) : Exception(message);

/// <summary>Perfil de processamento (seção 6.5): música ou locução de oferta.</summary>
public sealed record IngestionProfile(TrackKind Kind, TimeSpan MinDuration, TimeSpan MaxDuration, double TargetLufs, double FadeInS, double FadeOutS)
{
    public const double TruePeakDb = -1.5;

    public static IngestionProfile Music(double fadeInS = 0.5, double fadeOutS = 3) =>
        new(TrackKind.Music, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), -16, fadeInS, fadeOutS);

    public static IngestionProfile Offer() => new(TrackKind.Offer, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(120), -14, 0, 0);
}

public sealed record ProcessedAudio(string OutputPath, string Hash, string Title, string? Artist, long DurationMs, long SizeBytes, double? InputLufs);

/// <summary>
/// Pipeline de ingestão (seção 6.5): valida com ffprobe, corta o silêncio do início e do fim (−50 dB), aplica fades,
/// normaliza em duas passadas com loudnorm e codifica em AAC-LC 160 kbps, 44,1 kHz, estéreo, .m4a com faststart.
/// A saída é determinística (bitexact): o mesmo arquivo enviado duas vezes gera o mesmo hash. Na edição de música
/// (D34), os trechos são tirados logo depois do corte do silêncio, então os tempos são os da música processada.
/// </summary>
public sealed class AudioProcessor(Ffmpeg ffmpeg, ILogger<AudioProcessor> logger)
{
    public const long MaxUploadBytes = 100L * 1024 * 1024;

    public static readonly IReadOnlySet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus", ".wma",
    };

    private const string SilenceThreshold = "-50dB";

    /// <param name="cuts">Trechos a tirar (D34), em ms no tempo da música sem o silêncio; vazio na ingestão.</param>
    /// <param name="progress">Andamento de 0 a 1, chamado a cada etapa.</param>
    public async Task<ProcessedAudio> ProcessAsync(
        string inputPath, string originalName, IngestionProfile profile, string workDirectory, CancellationToken cancellationToken,
        IReadOnlyList<TrackCut>? cuts = null, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!AllowedExtensions.Contains(Path.GetExtension(originalName)))
        {
            throw new IngestionRejectedException($"Formato não aceito: {Path.GetExtension(originalName)}. Use mp3, m4a, aac, flac, wav, ogg, opus ou wma.");
        }

        if (new FileInfo(inputPath).Length > MaxUploadBytes)
        {
            throw new IngestionRejectedException("Arquivo maior que 100 MB.");
        }

        var probe = await ProbeAsync(inputPath, cancellationToken)
            ?? throw new IngestionRejectedException("O arquivo não é um áudio válido.");
        CheckDuration(probe.Duration, profile, "O áudio");

        Directory.CreateDirectory(workDirectory);
        var id = Guid.NewGuid().ToString("N");
        var trimmed = Path.Combine(workDirectory, id + "-corte.flac");
        var edited = Path.Combine(workDirectory, id + "-trechos.flac");
        var output = Path.Combine(workDirectory, id + ".m4a");
        try
        {
            progress?.Invoke(0.05);
            // Passo 3: corta o silêncio do início e, invertendo o áudio, o do fim.
            var silence = $"silenceremove=start_periods=1:start_threshold={SilenceThreshold}";
            await RunAsync(
                ["-i", inputPath, "-vn", "-map", "0:a:0", "-af", $"aformat=sample_fmts=fltp:sample_rates=44100:channel_layouts=stereo,{silence},areverse,{silence},areverse",
                 "-c:a", "flac", "-sample_fmt", "s32", trimmed],
                "cortar o silêncio", cancellationToken);
            var trimmedDuration = (await ProbeAsync(trimmed, cancellationToken))?.Duration ?? TimeSpan.Zero;
            CheckDuration(trimmedDuration, profile, "Sem o silêncio, o áudio");
            progress?.Invoke(0.3);

            // D34: tira os trechos marcados no editor, com um fade curto em cada emenda.
            var normalizedCuts = TrackCuts.Normalize(cuts ?? [], (long)trimmedDuration.TotalMilliseconds);
            if (normalizedCuts.Count > 0)
            {
                var kept = TrackCuts.Kept(normalizedCuts, (long)trimmedDuration.TotalMilliseconds);
                if (kept.Count == 0)
                {
                    throw new IngestionRejectedException("Sem os trechos marcados não sobra nada da música.");
                }

                await RunAsync(
                    ["-i", trimmed, "-filter_complex", TrackCuts.Filter(kept), "-map", "[out]", "-c:a", "flac", "-sample_fmt", "s32", edited],
                    "tirar os trechos", cancellationToken);
                File.Move(edited, trimmed, overwrite: true);
                trimmedDuration = (await ProbeAsync(trimmed, cancellationToken))?.Duration ?? TimeSpan.Zero;
                CheckDuration(trimmedDuration, profile, "Sem os trechos, a música");
            }

            progress?.Invoke(0.4);

            // Passos 4 e 5: fades e normalização em duas passadas (medição, depois ganho linear).
            var fades = Fades(profile, trimmedDuration);
            var target = string.Create(CultureInfo.InvariantCulture, $"loudnorm=I={profile.TargetLufs}:TP={IngestionProfile.TruePeakDb}:LRA=11");
            var measure = await RunAsync(["-i", trimmed, "-af", fades + target + ":print_format=json", "-f", "null", "-"], "medir o volume", cancellationToken);
            var loudness = ParseLoudness(measure.StandardError)
                ?? throw new InvalidOperationException("Não foi possível ler a medição do loudnorm.");
            if (!double.TryParse(loudness.InputI, NumberStyles.Float, CultureInfo.InvariantCulture, out var inputLufs) || double.IsInfinity(inputLufs))
            {
                throw new IngestionRejectedException("O áudio está em silêncio.");
            }

            progress?.Invoke(0.6);
            var normalize = target + string.Create(
                CultureInfo.InvariantCulture,
                $":measured_I={loudness.InputI}:measured_TP={loudness.InputTp}:measured_LRA={loudness.InputLra}:measured_thresh={loudness.InputThresh}:offset={loudness.TargetOffset}:linear=true");

            // Passo 6: AAC-LC 160 kbps, 44,1 kHz, estéreo, faststart, sem metadados e bit a bit reproduzível.
            await RunAsync(
                ["-i", trimmed, "-af", fades + normalize, "-ar", "44100", "-ac", "2", "-c:a", "aac", "-b:a", "160k",
                 "-movflags", "+faststart", "-map_metadata", "-1", "-fflags", "+bitexact", "-flags:a", "+bitexact", output],
                "codificar", cancellationToken);
            var final = await ProbeAsync(output, cancellationToken)
                ?? throw new InvalidOperationException("O arquivo processado não pôde ser lido pelo ffprobe.");
            progress?.Invoke(0.95);

            // Passo 7: SHA-256 do arquivo final (regra 7).
            string hash;
            await using (var stream = File.OpenRead(output))
            {
                hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            }

            var (title, artist) = Metadata(probe, originalName);
            logger.LogInformation(
                "Processado {Name}: {Duration:0.0} s, {Input:0.0} LUFS → {Target} LUFS, {Hash}",
                originalName, final.Duration.TotalSeconds, inputLufs, profile.TargetLufs, hash[..12]);
            return new ProcessedAudio(output, hash, title, artist, (long)final.Duration.TotalMilliseconds, new FileInfo(output).Length, Math.Round(inputLufs, 1));
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        finally
        {
            TryDelete(trimmed);
            TryDelete(edited);
        }
    }

    /// <summary>Título e artista das tags; sem tags, do nome do arquivo no formato "Artista - Título" (passo 2).</summary>
    internal static (string Title, string? Artist) Metadata(ProbeResult probe, string originalName)
    {
        if (probe.Title is { Length: > 0 } tagTitle)
        {
            return (tagTitle.Trim(), string.IsNullOrWhiteSpace(probe.Artist) ? null : probe.Artist.Trim());
        }

        // "01 - Artista - Título" e "01. Título": o número da faixa não faz parte do nome.
        var name = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(originalName).Trim(), @"^\d{1,3}\s*[-.]\s+", string.Empty);
        var separator = name.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0 ? (name[(separator + 3)..].Trim(), name[..separator].Trim()) : (name, null);
    }

    private static string Fades(IngestionProfile profile, TimeSpan duration)
    {
        if (profile.FadeInS <= 0 && profile.FadeOutS <= 0)
        {
            return string.Empty;
        }

        var fadeOutStart = Math.Max(0, duration.TotalSeconds - profile.FadeOutS);
        return string.Create(CultureInfo.InvariantCulture, $"afade=t=in:st=0:d={profile.FadeInS},afade=t=out:st={fadeOutStart:0.###}:d={profile.FadeOutS},");
    }

    private static void CheckDuration(TimeSpan duration, IngestionProfile profile, string subject)
    {
        if (duration < profile.MinDuration || duration > profile.MaxDuration)
        {
            throw new IngestionRejectedException(string.Create(
                CultureInfo.InvariantCulture,
                $"{subject} tem {Describe(duration)}; o aceito é de {Describe(profile.MinDuration)} a {Describe(profile.MaxDuration)}."));
        }
    }

    private static string Describe(TimeSpan duration) => duration.TotalSeconds < 90
        ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0} s")
        : string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMinutes:0.#} min");

    internal async Task<ProbeResult?> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ffmpeg.RunFfprobeAsync(["-print_format", "json", "-show_format", "-show_streams", path], cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || !streams.EnumerateArray().Any(s => s.TryGetProperty("codec_type", out var type) && type.GetString() == "audio"))
        {
            return null;
        }

        var format = root.GetProperty("format");
        var duration = format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        CollectTags(format, tags);
        foreach (var stream in streams.EnumerateArray())
        {
            CollectTags(stream, tags);
        }

        return new ProbeResult(duration, tags.GetValueOrDefault("title"), tags.GetValueOrDefault("artist"));
    }

    private static void CollectTags(JsonElement element, Dictionary<string, string> tags)
    {
        if (element.TryGetProperty("tags", out var values) && values.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in values.EnumerateObject())
            {
                if (tag.Value.ValueKind == JsonValueKind.String)
                {
                    tags.TryAdd(tag.Name, tag.Value.GetString()!);
                }
            }
        }
    }

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string step, CancellationToken cancellationToken)
    {
        var result = await ffmpeg.RunFfmpegAsync(arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            var tail = result.StandardError.Length > 600 ? result.StandardError[^600..] : result.StandardError;
            logger.LogWarning("ffmpeg falhou ao {Step} (código {Code}): {Error}", step, result.ExitCode, tail);
            throw new IngestionRejectedException($"Não foi possível {step} o áudio. O arquivo pode estar corrompido.");
        }

        return result;
    }

    internal static LoudnessMeasurement? ParseLoudness(string stderr)
    {
        var start = stderr.LastIndexOf('{');
        var end = stderr.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LoudnessMeasurement>(stderr[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Não foi possível apagar o temporário {Path}", path);
        }
    }

    internal sealed record ProbeResult(TimeSpan Duration, string? Title, string? Artist);

    internal sealed record LoudnessMeasurement(
        [property: System.Text.Json.Serialization.JsonPropertyName("input_i")] string InputI,
        [property: System.Text.Json.Serialization.JsonPropertyName("input_tp")] string InputTp,
        [property: System.Text.Json.Serialization.JsonPropertyName("input_lra")] string InputLra,
        [property: System.Text.Json.Serialization.JsonPropertyName("input_thresh")] string InputThresh,
        [property: System.Text.Json.Serialization.JsonPropertyName("target_offset")] string TargetOffset);
}
