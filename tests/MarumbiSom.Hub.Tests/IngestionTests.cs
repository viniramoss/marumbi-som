using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Library;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Tests;

/// <summary>
/// Ingestão com o ffmpeg de verdade (seção 6.5 e aceite da Etapa 3). Os áudios de entrada são gerados pelo próprio
/// ffmpeg: tons com silêncio nas pontas, volumes e formatos diferentes, fim cortado seco.
/// </summary>
public sealed class IngestionTests : IAsyncLifetime
{
    private HubHarness _hub = null!;
    private HttpClient _admin = null!;
    private string _inputs = null!;

    public async Task InitializeAsync()
    {
        _hub = await HubHarness.StartAsync();
        _admin = await _hub.AdminClientAsync();
        _inputs = Path.Combine(_hub.Root, "entradas");
        Directory.CreateDirectory(_inputs);
    }

    public async Task DisposeAsync() => await _hub.DisposeAsync();

    [Fact]
    public async Task Musica_sai_sem_silencio_com_fade_out_e_volume_normalizado()
    {
        // 2 s de silêncio + 40 s de tom alto terminando seco + 3 s de silêncio.
        var input = await GenerateAsync("01 - Banda X - Corte Seco.mp3", toneSeconds: 40, amplitude: 0.9, leadSilence: 2, tailSilence: 3);

        var job = await UploadAndWaitAsync(input);

        Assert.Equal(IngestionStatus.Done, job.Status);
        var track = await _hub.WithDbAsync(db => db.Tracks.SingleAsync(t => t.Hash == job.Hash));
        Assert.Equal("Corte Seco", track.Title);
        Assert.Equal("Banda X", track.Artist);
        Assert.InRange(track.DurationMs, 39_500, 40_500);
        var output = _hub.Paths.LibraryFile(track.Hash);
        Assert.True(File.Exists(output));
        Assert.True(File.Exists(Path.Combine(_hub.Paths.Originals, track.Hash + ".mp3")));
        var probe = await FfprobeAsync(output);
        Assert.Equal("aac", probe.Codec);
        Assert.Equal(44100, probe.SampleRate);
        Assert.Equal(2, probe.Channels);
        Assert.InRange(probe.BitRate, 140_000, 180_000);
        Assert.InRange(await IntegratedLoudnessAsync(output), -17, -15);
        // Fade-out de 3 s: o último meio segundo fica bem mais baixo que o meio da música.
        var middle = await MeanVolumeAsync(output, 15, 1);
        var end = await MeanVolumeAsync(output, (track.DurationMs / 1000.0) - 0.6, 0.5);
        Assert.True(middle - end > 10, $"meio {middle} dB, fim {end} dB");
        Assert.Empty(Directory.EnumerateFiles(_hub.Paths.Incoming));
        Assert.Empty(Directory.EnumerateFiles(_hub.Paths.Work));
    }

    [Fact]
    public async Task Formatos_e_volumes_diferentes_saem_no_mesmo_volume_percebido()
    {
        var quiet = await GenerateAsync("baixinho.wav", toneSeconds: 35, amplitude: 0.05, frequency: 330);
        var loud = await GenerateAsync("altao.ogg", toneSeconds: 35, amplitude: 0.95, frequency: 550);
        var flac = await GenerateAsync("Artista Y - Meio.flac", toneSeconds: 35, amplitude: 0.3, frequency: 660);

        var jobs = new[] { await UploadAndWaitAsync(quiet), await UploadAndWaitAsync(loud), await UploadAndWaitAsync(flac) };

        Assert.All(jobs, j => Assert.Equal(IngestionStatus.Done, j.Status));
        foreach (var job in jobs)
        {
            Assert.InRange(await IntegratedLoudnessAsync(_hub.Paths.LibraryFile(job.Hash!)), -17, -15);
        }

        var tracks = await _hub.WithDbAsync(db => db.Tracks.ToListAsync());
        Assert.Contains(tracks, t => t.Title == "Meio" && t.Artist == "Artista Y");
        Assert.True(tracks.Single(t => t.OriginalName == "baixinho.wav").LoudnessLufs < tracks.Single(t => t.OriginalName == "altao.ogg").LoudnessLufs);
    }

    [Fact]
    public async Task Mesmo_arquivo_duas_vezes_vira_duplicata()
    {
        var input = await GenerateAsync("repetida.mp3", toneSeconds: 31, amplitude: 0.5);

        var first = await UploadAndWaitAsync(input);
        var second = await UploadAndWaitAsync(input);

        Assert.Equal(IngestionStatus.Done, first.Status);
        Assert.Equal(IngestionStatus.Duplicate, second.Status);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(1, await _hub.WithDbAsync(db => db.Tracks.CountAsync()));
    }

    [Fact]
    public async Task Audio_curto_ou_invalido_e_recusado_com_motivo()
    {
        var shortInput = await GenerateAsync("curta.mp3", toneSeconds: 10, amplitude: 0.5);
        var fake = Path.Combine(_inputs, "nao-e-audio.mp3");
        await File.WriteAllTextAsync(fake, "isto não é um mp3");

        var tooShort = await UploadAndWaitAsync(shortInput);
        var invalid = await UploadAndWaitAsync(fake);
        var wrongType = await UploadRawAsync(Path.Combine(_inputs, "planilha.xlsx"), "conteúdo");

        Assert.Equal(IngestionStatus.Rejected, tooShort.Status);
        Assert.Contains("30 s", tooShort.Error);
        Assert.Equal(IngestionStatus.Rejected, invalid.Status);
        Assert.Equal("rejected", wrongType[0].GetProperty("status").GetString());
        Assert.Equal(0, await _hub.WithDbAsync(db => db.Tracks.CountAsync()));
    }

    [Fact]
    public async Task Oferta_usa_o_perfil_de_locucao_sem_fade_e_mais_alta()
    {
        var input = await GenerateAsync("Picanha 69,90.wav", toneSeconds: 8, amplitude: 0.2, leadSilence: 1);

        var job = await UploadAndWaitAsync(input, kind: "offer");

        Assert.Equal(IngestionStatus.Done, job.Status);
        var track = await _hub.WithDbAsync(db => db.Tracks.SingleAsync());
        Assert.Equal(TrackKind.Offer, track.Kind);
        Assert.InRange(track.DurationMs, 7_500, 8_500);
        var output = _hub.Paths.LibraryFile(track.Hash);
        Assert.InRange(await IntegratedLoudnessAsync(output), -15, -13);
        var middle = await MeanVolumeAsync(output, 3, 1);
        var end = await MeanVolumeAsync(output, 7.3, 0.5);
        Assert.True(Math.Abs(middle - end) < 2, $"sem fade: meio {middle} dB, fim {end} dB");
        var music = await HubHarness.JsonAsync(await _admin.GetAsync("/api/tracks"));
        Assert.Empty(music.EnumerateArray());
    }

    private async Task<IngestionJob> UploadAndWaitAsync(string path, string kind = "music")
    {
        var queue = _hub.App.Services.GetRequiredService<IngestionWorker>();
        var jobs = await UploadRawAsync(path, null, kind);
        var id = jobs[0].GetProperty("id").GetString()!;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (queue.Find(id) is { Status: not (IngestionStatus.Queued or IngestionStatus.Processing) } job)
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Ingestão de {path} não terminou.");
    }

    private async Task<JsonElement[]> UploadRawAsync(string path, string? content, string kind = "music")
    {
        if (content is not null)
        {
            await File.WriteAllTextAsync(path, content);
        }

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "files", Path.GetFileName(path));
        form.Add(new StringContent(kind), "kind");
        var response = await _admin.PostAsync("/api/tracks", form);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await HubHarness.JsonAsync(response)).EnumerateArray().ToArray();
    }

    private async Task<string> GenerateAsync(string name, double toneSeconds, double amplitude, double leadSilence = 0, double tailSilence = 0, int frequency = 440)
    {
        var output = Path.Combine(_inputs, name);
        var inv = CultureInfo.InvariantCulture;
        var filter = string.Format(
            inv,
            "sine=frequency={0}:sample_rate=48000:duration={1},volume={2},aformat=channel_layouts=stereo,adelay={3}|{3},apad=pad_dur={4}",
            frequency, toneSeconds, amplitude, (int)(leadSilence * 1000), tailSilence);
        var result = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", filter, "-metadata", "comment=teste", output]);
        Assert.True(result.ExitCode == 0, result.Error);
        return output;
    }

    private static async Task<double> IntegratedLoudnessAsync(string path)
    {
        var result = await RunAsync("ffmpeg", ["-hide_banner", "-nostdin", "-i", path, "-af", "loudnorm=print_format=json", "-f", "null", "-"]);
        var json = result.Error[result.Error.LastIndexOf('{')..(result.Error.LastIndexOf('}') + 1)];
        return double.Parse(JsonDocument.Parse(json).RootElement.GetProperty("input_i").GetString()!, CultureInfo.InvariantCulture);
    }

    private static async Task<double> MeanVolumeAsync(string path, double start, double duration)
    {
        var inv = CultureInfo.InvariantCulture;
        var result = await RunAsync("ffmpeg", ["-hide_banner", "-nostdin", "-ss", start.ToString(inv), "-t", duration.ToString(inv), "-i", path, "-af", "volumedetect", "-f", "null", "-"]);
        var line = result.Error.Split('\n').Single(l => l.Contains("mean_volume:", StringComparison.Ordinal));
        return double.Parse(line[(line.IndexOf("mean_volume:", StringComparison.Ordinal) + 12)..].Replace("dB", string.Empty, StringComparison.Ordinal).Trim(), inv);
    }

    private static async Task<(string Codec, int SampleRate, int Channels, int BitRate)> FfprobeAsync(string path)
    {
        var result = await RunAsync("ffprobe", ["-v", "error", "-print_format", "json", "-show_streams", path]);
        var stream = JsonDocument.Parse(result.Output).RootElement.GetProperty("streams")[0];
        return (stream.GetProperty("codec_name").GetString()!, int.Parse(stream.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture),
            stream.GetProperty("channels").GetInt32(), int.Parse(stream.GetProperty("bit_rate").GetString()!, CultureInfo.InvariantCulture));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}
