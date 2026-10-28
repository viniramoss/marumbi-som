using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        // D31: o título é o número de importação; o nome do arquivo fica guardado.
        Assert.Equal((1, "001", null), (track.Number, track.Title, track.Artist));
        Assert.Equal("01 - Banda X - Corte Seco.mp3", track.OriginalName);
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
        Assert.Equal(["001", "002", "003"], tracks.OrderBy(t => t.Number).Select(t => t.Title));
        Assert.Equal("Artista Y - Meio.flac", tracks.Single(t => t.Number == 3).OriginalName);
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
    public async Task Numero_da_musica_removida_so_volta_com_o_mesmo_audio()
    {
        var first = await UploadAndWaitAsync(await GenerateAsync("primeira.mp3", toneSeconds: 31, amplitude: 0.5, frequency: 330));
        var removed = await UploadAndWaitAsync(await GenerateAsync("removida.mp3", toneSeconds: 31, amplitude: 0.5, frequency: 550));
        (await _admin.DeleteAsync($"/api/tracks/{removed.Hash}")).EnsureSuccessStatusCode();

        // Outra música depois de remover a 002 ganha a 003; a 002 fica guardada para o mesmo áudio (D31).
        var other = await UploadAndWaitAsync(await GenerateAsync("outra.mp3", toneSeconds: 31, amplitude: 0.5, frequency: 660));
        var back = await UploadAndWaitAsync(await GenerateAsync("removida de novo.mp3", toneSeconds: 31, amplitude: 0.5, frequency: 550));

        Assert.All(new[] { first, removed, other, back }, j => Assert.Equal(IngestionStatus.Done, j.Status));
        Assert.Equal(removed.Hash, back.Hash);
        var tracks = await _hub.WithDbAsync(db => db.Tracks.ToDictionaryAsync(t => t.Hash));
        Assert.Equal((1, 2, 3), (tracks[first.Hash!].Number, tracks[removed.Hash!].Number, tracks[other.Hash!].Number));
        Assert.Null(tracks[removed.Hash!].DeletedAt);
    }

    [Fact]
    public async Task Edicao_tira_o_trecho_e_a_musica_editada_toma_o_lugar_nas_playlists_e_ancoras()
    {
        // 15 s alto, 10 s quase mudo, 25 s alto: tirar 15–25 s deixa só o som alto.
        var input = await GenerateSectionsAsync("com-trecho.mp3", (15, 0.5), (10, 0.02), (25, 0.5));
        var original = await UploadAndWaitAsync(input);
        var hash = original.Hash!;
        await _hub.WithDbAsync(async db =>
        {
            db.Tracks.Add(new Data.Track { Hash = "oferta-teste", Kind = TrackKind.Offer, Title = "Oferta", OriginalName = "oferta.mp3", DurationMs = 8_000 });
            var playlist = new Data.Playlist { Name = "Manhã" };
            db.Playlists.Add(playlist);
            await db.SaveChangesAsync();
            db.PlaylistItems.Add(new Data.PlaylistItem { Id = "item-1", PlaylistId = playlist.Id, Position = 0, TrackHash = hash });
            var offer = new Data.Offer { Name = "Picanha", AudioHash = "oferta-teste", ValidFrom = DateTimeOffset.UtcNow, ValidUntil = DateTimeOffset.UtcNow.AddDays(7) };
            db.Offers.Add(offer);
            await db.SaveChangesAsync();
            foreach (var offset in new long[] { 5_000, 20_000, 30_000 })
            {
                db.OfferPlacements.Add(new Data.OfferPlacement
                {
                    OfferId = offer.Id, Kind = PlacementKinds.AnchorDuring, PlaylistId = playlist.Id, PlaylistItemId = "item-1", OffsetMs = offset,
                });
            }

            await db.SaveChangesAsync();
        });

        var job = await EditAndWaitAsync(hash, new { cuts = new[] { new { startMs = 15_000, endMs = 25_000 } } });

        Assert.Equal(IngestionStatus.Done, job.Status);
        Assert.NotEqual(hash, job.Hash);
        var list = (await HubHarness.JsonAsync(await _admin.GetAsync("/api/tracks"))).EnumerateArray().ToArray();
        var edited = Assert.Single(list);
        Assert.Equal((job.Hash, 1, "001", true), (edited.GetProperty("hash").GetString(), edited.GetProperty("number").GetInt32(),
            edited.GetProperty("title").GetString(), edited.GetProperty("edited").GetBoolean()));
        Assert.InRange(edited.GetProperty("durationMs").GetInt64(), 39_700, 40_300);
        Assert.Equal(10_000, edited.GetProperty("removedMs").GetInt64());
        Assert.Equal("Manhã", edited.GetProperty("playlists")[0].GetProperty("name").GetString());

        var tracks = await _hub.WithDbAsync(db => db.Tracks.ToDictionaryAsync(t => t.Hash));
        Assert.NotNull(tracks[hash].DeletedAt);
        Assert.Null(tracks[hash].Number);
        Assert.Equal((hash, "[[15000,25000]]"), (tracks[job.Hash!].SourceHash, tracks[job.Hash!].CutsJson));
        Assert.Equal(job.Hash, await _hub.WithDbAsync(db => db.PlaylistItems.Select(i => i.TrackHash).SingleAsync()));
        // A âncora dentro do trecho tirado vai para a emenda; a de depois anda 10 s.
        Assert.Equal([5_000L, 15_000L, 20_000L], await _hub.WithDbAsync(db => db.OfferPlacements.OrderBy(p => p.Id).Select(p => p.OffsetMs!.Value).ToListAsync()));

        // Onde era o trecho quase mudo agora toca o som alto.
        var before = await MeanVolumeAsync(_hub.Paths.LibraryFile(hash), 17, 3);
        var after = await MeanVolumeAsync(_hub.Paths.LibraryFile(job.Hash!), 17, 3);
        Assert.True(after - before > 15, $"antes {before} dB, depois {after} dB");
    }

    [Fact]
    public async Task Editar_de_novo_soma_os_cortes_e_voltar_para_a_original_traz_o_mesmo_arquivo()
    {
        var input = await GenerateAsync("longa.mp3", toneSeconds: 60, amplitude: 0.5);
        var original = (await UploadAndWaitAsync(input)).Hash!;

        var first = await EditAndWaitAsync(original, new { cuts = new[] { new { startMs = 0, endMs = 5_000 } } });
        var second = await EditAndWaitAsync(first.Hash!, new { cuts = new[] { new { startMs = 10_000, endMs = 20_000 } } });

        var edited = await _hub.WithDbAsync(db => db.Tracks.SingleAsync(t => t.Hash == second.Hash));
        Assert.Equal((1, original, "[[0,5000],[15000,25000]]"), (edited.Number, edited.SourceHash, edited.CutsJson));
        Assert.InRange(edited.DurationMs, 44_700, 45_300);

        // O arquivo enviado de uma música editada continua sendo ela.
        Assert.Equal(IngestionStatus.Duplicate, (await UploadAndWaitAsync(input)).Status);

        var tooMuch = await _admin.PostAsJsonAsync($"/api/tracks/{second.Hash}/edit", new { cuts = new[] { new { startMs = 0, endMs = 40_000 } } });
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.Contains("mínimo é 30 s", await tooMuch.Content.ReadAsStringAsync());

        var back = await EditAndWaitAsync(second.Hash!, new { original = true });

        Assert.Equal(original, back.Hash);
        var tracks = await _hub.WithDbAsync(db => db.Tracks.ToDictionaryAsync(t => t.Hash));
        Assert.Equal((1, (string?)null, (string?)null, (DateTimeOffset?)null), (tracks[original].Number, tracks[original].SourceHash, tracks[original].CutsJson, tracks[original].DeletedAt));
        Assert.All(new[] { first.Hash!, second.Hash! }, h => Assert.Null(tracks[h].Number));
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
        var jobs = await UploadRawAsync(path, null, kind);
        return await WaitAsync(jobs[0].GetProperty("id").GetString()!);
    }

    private async Task<IngestionJob> EditAndWaitAsync(string hash, object body)
    {
        var response = await _admin.PostAsJsonAsync($"/api/tracks/{hash}/edit", body);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var id = (await HubHarness.JsonAsync(response)).GetProperty("id").GetString()!;
        return await WaitAsync(id);
    }

    private async Task<IngestionJob> WaitAsync(string id)
    {
        var queue = _hub.App.Services.GetRequiredService<IngestionWorker>();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (queue.Find(id) is { Status: not (IngestionStatus.Queued or IngestionStatus.Processing) } job)
            {
                return job;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"O trabalho {id} não terminou.");
    }

    private async Task<string> GenerateSectionsAsync(string name, params (double Seconds, double Amplitude)[] sections)
    {
        var output = Path.Combine(_inputs, name);
        var inv = CultureInfo.InvariantCulture;
        var parts = sections.Select((section, i) => string.Format(inv, "sine=frequency=440:sample_rate=48000:duration={0},volume={1}[s{2}]", section.Seconds, section.Amplitude, i));
        var filter = string.Join(';', parts) + ";" + string.Concat(sections.Select((_, i) => $"[s{i}]")) + $"concat=n={sections.Length}:v=0:a=1,aformat=channel_layouts=stereo";
        var result = await RunAsync("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-filter_complex", filter, output]);
        Assert.True(result.ExitCode == 0, result.Error);
        return output;
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
