using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Data;
using MarumbiSom.Hub.Manifests;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Library;

public enum IngestionStatus
{
    Queued,
    Processing,
    Done,
    Duplicate,
    Rejected,
    Failed,
}

public sealed record IngestionJob
{
    public required string Id { get; init; }

    public required string OriginalName { get; init; }

    public required TrackKind Kind { get; init; }

    public required DateTimeOffset UploadedAt { get; init; }

    public IngestionStatus Status { get; set; }

    public string? Error { get; set; }

    public string? Hash { get; set; }

    /// <summary>Andamento do processamento, de 0 a 1 (D34).</summary>
    public double Progress { get; set; }

    /// <summary>Edição de música (D34): hash da música que vai ser trocada pela editada; nulo nos envios.</summary>
    public string? EditOf { get; init; }

    /// <summary>Edição: hash da música enviada de onde a editada é refeita.</summary>
    public string? SourceHash { get; init; }

    /// <summary>Edição: trechos tirados, no tempo da música enviada.</summary>
    public IReadOnlyList<TrackCut>? Cuts { get; init; }

    /// <summary>Edição: extensão do arquivo copiado para data\incoming (o original ou o da biblioteca).</summary>
    public string? InputExtension { get; init; }

    /// <summary>Arquivo recusado antes de entrar na fila (formato ou tamanho).</summary>
    public static IngestionJob Rejected(string originalName, TrackKind kind, DateTimeOffset at, string error) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        OriginalName = Path.GetFileName(originalName),
        Kind = kind,
        UploadedAt = at,
        Status = IngestionStatus.Rejected,
        Error = error,
    };
}

/// <summary>
/// Fila de ingestão (seção 6.5): uma música por vez, em segundo plano. O upload fica em data\incoming com um arquivo
/// .json ao lado; assim um reinício do Hub retoma o que estava na fila.
/// </summary>
public sealed class IngestionWorker(
    HubPaths paths,
    AudioProcessor processor,
    IServiceScopeFactory scopes,
    ManifestPublisher publisher,
    TimeProvider time,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private const int MaxJobsKept = 500;
    private readonly Channel<IngestionJob> _queue = Channel.CreateUnbounded<IngestionJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, IngestionJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>Disparado quando um trabalho termina (com sucesso ou não).</summary>
    public event EventHandler<IngestionJob>? Completed;

    /// <summary>Disparado a cada mudança de situação de um trabalho (fila, processando, fim); usado pelo painel ao vivo.</summary>
    public event EventHandler<IngestionJob>? Changed;

    public IReadOnlyList<IngestionJob> Jobs => _jobs.Values.OrderByDescending(j => j.UploadedAt).ToList();

    public IngestionJob? Find(string id) => _jobs.GetValueOrDefault(id);

    /// <summary>Copia o upload para data\incoming e põe na fila.</summary>
    public async Task<IngestionJob> EnqueueAsync(Stream content, string originalName, TrackKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var job = new IngestionJob
        {
            Id = Guid.NewGuid().ToString("N"),
            OriginalName = Path.GetFileName(originalName),
            Kind = kind,
            UploadedAt = time.GetUtcNow(),
            Status = IngestionStatus.Queued,
        };
        Directory.CreateDirectory(paths.Incoming);
        var audioPath = AudioPathFor(job);
        await using (var file = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        await File.WriteAllTextAsync(SidecarPathFor(job.Id), JsonSerializer.Serialize(job, HubJson.Options), cancellationToken);
        Track(job);
        await _queue.Writer.WriteAsync(job, cancellationToken);
        logger.LogInformation("Upload recebido: {Name} ({Kind}), na fila", job.OriginalName, kind);
        Changed?.Invoke(this, job);
        return job;
    }

    /// <summary>
    /// Põe na fila a edição de uma música (D34): copia o arquivo de onde ela é refeita para data\incoming, para a edição
    /// sobreviver a um reinício do Hub como os envios.
    /// </summary>
    public async Task<IngestionJob> EnqueueEditAsync(
        Data.Track track, string sourceHash, string sourcePath, IReadOnlyList<TrackCut> cuts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        var job = new IngestionJob
        {
            Id = Guid.NewGuid().ToString("N"),
            OriginalName = track.OriginalName,
            Kind = track.Kind,
            UploadedAt = time.GetUtcNow(),
            Status = IngestionStatus.Queued,
            EditOf = track.Hash,
            SourceHash = sourceHash,
            Cuts = cuts,
            InputExtension = Path.GetExtension(sourcePath).ToLowerInvariant(),
        };
        Directory.CreateDirectory(paths.Incoming);
        await using (var source = File.OpenRead(sourcePath))
        await using (var file = new FileStream(AudioPathFor(job), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(file, cancellationToken);
        }

        await File.WriteAllTextAsync(SidecarPathFor(job.Id), JsonSerializer.Serialize(job, HubJson.Options), cancellationToken);
        Track(job);
        await _queue.Writer.WriteAsync(job, cancellationToken);
        logger.LogInformation("Edição de {Title} na fila: {Count} trecho(s) tirado(s) do original", track.Title, cuts.Count);
        Changed?.Invoke(this, job);
        return job;
    }

    /// <summary>Edição de música ainda na fila ou processando.</summary>
    public bool IsEditing(string hash) =>
        _jobs.Values.Any(j => j.EditOf == hash && j.Status is IngestionStatus.Queued or IngestionStatus.Processing);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        foreach (var sidecar in Directory.EnumerateFiles(paths.Incoming, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                var job = JsonSerializer.Deserialize<IngestionJob>(File.ReadAllText(sidecar), HubJson.Options);
                if (job is not null && File.Exists(AudioPathFor(job)))
                {
                    job.Status = IngestionStatus.Queued;
                    Track(job);
                    _queue.Writer.TryWrite(job);
                    continue;
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Arquivo de fila ilegível: {Path}", sidecar);
            }

            File.Delete(sidecar);
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await ProcessAsync(job, stoppingToken);
        }
    }

    internal async Task ProcessAsync(IngestionJob job, CancellationToken cancellationToken)
    {
        job.Status = IngestionStatus.Processing;
        Changed?.Invoke(this, job);
        var audioPath = AudioPathFor(job);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDb>();
            var profile = job.Kind == TrackKind.Offer ? IngestionProfile.Offer() : await MusicProfileAsync(db, cancellationToken);
            var processed = await processor.ProcessAsync(
                audioPath, job.OriginalName, profile, paths.Work, cancellationToken, job.Cuts, progress => Report(job, progress));
            job.Hash = processed.Hash;
            if (job.EditOf is not null)
            {
                await ReplaceEditedAsync(job, processed, db, cancellationToken);
            }
            else
            {
                await StoreUploadAsync(job, processed, db, cancellationToken);
            }
        }
        catch (IngestionRejectedException ex)
        {
            job.Status = IngestionStatus.Rejected;
            job.Error = ex.Message;
            logger.LogWarning("Upload recusado: {Name}: {Reason}", job.OriginalName, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            job.Status = IngestionStatus.Failed;
            job.Error = "Erro inesperado ao processar o arquivo. Veja o log do Hub.";
            logger.LogError(ex, "Falha ao processar {Name}", job.OriginalName);
        }

        if (job.Status != IngestionStatus.Processing)
        {
            TryDelete(audioPath);
            TryDelete(SidecarPathFor(job.Id));
            Completed?.Invoke(this, job);
            Changed?.Invoke(this, job);
        }
    }

    /// <summary>Passos 7 e 8 da ingestão: duplicata, música nova ou volta de uma removida.</summary>
    private async Task StoreUploadAsync(IngestionJob job, ProcessedAudio processed, HubDb db, CancellationToken cancellationToken)
    {
        var existing = await db.Tracks.SingleOrDefaultAsync(t => t.Hash == processed.Hash, cancellationToken);
        var libraryFile = paths.LibraryFile(processed.Hash);
        var editedVersion = existing is { DeletedAt: not null }
            ? await db.Tracks.FirstOrDefaultAsync(t => t.SourceHash == existing.Hash && t.DeletedAt == null, cancellationToken)
            : null;
        if (existing is not null && existing.DeletedAt is null && File.Exists(libraryFile))
        {
            // Passo 7: o mesmo áudio já está na biblioteca.
            File.Delete(processed.OutputPath);
            job.Status = IngestionStatus.Duplicate;
            logger.LogInformation("{Name} já está na biblioteca como {Title}", job.OriginalName, existing.Title);
        }
        else if (editedVersion is not null)
        {
            // D34: o arquivo enviado de uma música editada continua sendo ela (para voltar, há "Voltar para a original").
            File.Delete(processed.OutputPath);
            job.Status = IngestionStatus.Duplicate;
            logger.LogInformation("{Name} já está na biblioteca, editada, como {Title}", job.OriginalName, editedVersion.Title);
        }
        else
        {
            // Passo 8: library\ab\hash.m4a e o original em originals\.
            Directory.CreateDirectory(Path.GetDirectoryName(libraryFile)!);
            File.Move(processed.OutputPath, libraryFile, overwrite: true);
            var original = Path.Combine(paths.Originals, processed.Hash + Path.GetExtension(job.OriginalName).ToLowerInvariant());
            if (!File.Exists(original))
            {
                File.Copy(AudioPathFor(job), original);
            }

            if (existing is null)
            {
                // D31: a música ganha o próximo número como título (as tags costumam vir iguais); oferta usa o nome.
                int? number = job.Kind == TrackKind.Music ? (await db.Tracks.MaxAsync(t => t.Number, cancellationToken) ?? 0) + 1 : null;
                db.Tracks.Add(new Data.Track
                {
                    Hash = processed.Hash,
                    Kind = job.Kind,
                    Number = number,
                    Title = number is { } n ? Data.Track.NumberTitle(n) : processed.Title,
                    Artist = number is null ? processed.Artist : null,
                    DurationMs = processed.DurationMs,
                    SizeBytes = processed.SizeBytes,
                    LoudnessLufs = processed.InputLufs,
                    OriginalName = job.OriginalName,
                    CreatedAt = time.GetUtcNow(),
                });
            }
            else
            {
                // Volta a música removida antes (exclusão lógica). Uma trocada pela editada (D34) ficou sem número.
                existing.DeletedAt = null;
                if (existing.Kind == TrackKind.Music && existing.Number is null)
                {
                    existing.Number = (await db.Tracks.MaxAsync(t => t.Number, cancellationToken) ?? 0) + 1;
                }

                existing.SizeBytes = processed.SizeBytes;
                existing.DurationMs = processed.DurationMs;
            }

            await db.SaveChangesAsync(cancellationToken);
            job.Status = IngestionStatus.Done;
            if (existing is not null)
            {
                await publisher.RegenerateAllAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Troca a música pela editada (D34): a nova herda número, título, artista e data, entra nas playlists no lugar da
    /// anterior (os itens mantêm o id, então as âncoras continuam) e as âncoras "durante" andam com o corte.
    /// </summary>
    private async Task ReplaceEditedAsync(IngestionJob job, ProcessedAudio processed, HubDb db, CancellationToken cancellationToken)
    {
        var current = await db.Tracks.SingleOrDefaultAsync(t => t.Hash == job.EditOf && t.DeletedAt == null, cancellationToken);
        if (current is null)
        {
            File.Delete(processed.OutputPath);
            throw new IngestionRejectedException("A música foi removida ou trocada enquanto a edição esperava na fila.");
        }

        if (processed.Hash == current.Hash)
        {
            File.Delete(processed.OutputPath);
            job.Status = IngestionStatus.Done;
            return;
        }

        var libraryFile = paths.LibraryFile(processed.Hash);
        Directory.CreateDirectory(Path.GetDirectoryName(libraryFile)!);
        File.Move(processed.OutputPath, libraryFile, overwrite: true);

        var sourceHash = job.SourceHash ?? current.Hash;
        var oldCuts = TrackCuts.Parse(current.CutsJson);
        var newCuts = job.Cuts ?? [];
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // O número é único: a anterior solta o número antes de a nova recebê-lo.
        var number = current.Number;
        current.Number = null;
        current.DeletedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        var target = await db.Tracks.SingleOrDefaultAsync(t => t.Hash == processed.Hash, cancellationToken);
        if (target is null)
        {
            target = new Data.Track
            {
                Hash = processed.Hash,
                Kind = current.Kind,
                Title = current.Title,
                OriginalName = current.OriginalName,
                CreatedAt = current.CreatedAt,
            };
            db.Tracks.Add(target);
        }

        if (target.DeletedAt is not null || target.Number is null)
        {
            target.DeletedAt = null;
            target.Number = number;
            target.Title = current.Title;
            target.Artist = current.Artist;
        }

        target.DurationMs = processed.DurationMs;
        target.SizeBytes = processed.SizeBytes;
        target.LoudnessLufs = current.LoudnessLufs;
        target.SourceHash = processed.Hash == sourceHash ? null : sourceHash;
        target.CutsJson = target.SourceHash is null ? null : TrackCuts.Serialize(newCuts);
        await db.SaveChangesAsync(cancellationToken);

        var items = await db.PlaylistItems.Where(i => i.TrackHash == current.Hash).ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            item.TrackHash = target.Hash;
        }

        var itemIds = items.Select(i => i.Id).ToList();
        var maxOffset = Math.Max(0, target.DurationMs - (long)ManifestGenerator.AnchorEndMargin.TotalMilliseconds);
        var anchors = await db.OfferPlacements
            .Where(p => p.Kind == PlacementKinds.AnchorDuring && p.PlaylistItemId != null && itemIds.Contains(p.PlaylistItemId) && p.OffsetMs != null)
            .ToListAsync(cancellationToken);
        foreach (var anchor in anchors)
        {
            anchor.OffsetMs = Math.Clamp(TrackCuts.FromSource(TrackCuts.ToSource(anchor.OffsetMs!.Value, oldCuts), newCuts), 0, maxOffset);
        }

        var playlistIds = items.Select(i => i.PlaylistId).Distinct().ToList();
        await db.Playlists.Where(p => playlistIds.Contains(p.Id)).ExecuteUpdateAsync(p => p.SetProperty(x => x.UpdatedAt, now), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        job.Hash = target.Hash;
        job.Status = IngestionStatus.Done;
        logger.LogInformation(
            "{Title} editada: {From} → {To}, {Removed:0.0} s tirados do original, {Items} item(ns) de playlist e {Anchors} âncora(s) ajustados",
            target.Title, current.Hash[..12], target.Hash[..12], TrackCuts.RemovedMs(newCuts) / 1000.0, items.Count, anchors.Count);
        await publisher.RegenerateAllAsync(cancellationToken);
    }

    private void Report(IngestionJob job, double progress)
    {
        job.Progress = progress;
        Changed?.Invoke(this, job);
    }

    private static async Task<IngestionProfile> MusicProfileAsync(HubDb db, CancellationToken cancellationToken)
    {
        // Os tempos de fade ficam em settings e valem para novas ingestões (seção 6.5, passo 4).
        var settings = await db.Settings.Where(s => s.Key == SettingKeys.FadeInS || s.Key == SettingKeys.FadeOutS)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        return IngestionProfile.Music(
            SettingKeys.Number(settings, SettingKeys.FadeInS, 0.5, 0, 10),
            SettingKeys.Number(settings, SettingKeys.FadeOutS, 3, 0, 15));
    }

    private void Track(IngestionJob job)
    {
        _jobs[job.Id] = job;
        if (_jobs.Count > MaxJobsKept)
        {
            foreach (var old in _jobs.Values.Where(j => j.Status is not (IngestionStatus.Queued or IngestionStatus.Processing))
                         .OrderBy(j => j.UploadedAt).Take(_jobs.Count - MaxJobsKept))
            {
                _jobs.TryRemove(old.Id, out _);
            }
        }
    }

    private string AudioPathFor(IngestionJob job) =>
        Path.Combine(paths.Incoming, job.Id + (job.InputExtension ?? Path.GetExtension(job.OriginalName).ToLowerInvariant()));

    private string SidecarPathFor(string id) => Path.Combine(paths.Incoming, id + ".json");

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Não foi possível apagar {Path}", path);
        }
    }
}
