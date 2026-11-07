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
            var processed = await processor.ProcessAsync(audioPath, job.OriginalName, profile, paths.Work, cancellationToken);
            job.Hash = processed.Hash;

            var existing = await db.Tracks.SingleOrDefaultAsync(t => t.Hash == processed.Hash, cancellationToken);
            var libraryFile = paths.LibraryFile(processed.Hash);
            if (existing is not null && existing.DeletedAt is null && File.Exists(libraryFile))
            {
                // Passo 7: o mesmo áudio já está na biblioteca.
                File.Delete(processed.OutputPath);
                job.Status = IngestionStatus.Duplicate;
                logger.LogInformation("{Name} já está na biblioteca como {Title}", job.OriginalName, existing.Title);
            }
            else
            {
                // Passo 8: library\ab\hash.m4a e o original em originals\.
                Directory.CreateDirectory(Path.GetDirectoryName(libraryFile)!);
                File.Move(processed.OutputPath, libraryFile, overwrite: true);
                var original = Path.Combine(paths.Originals, processed.Hash + Path.GetExtension(job.OriginalName).ToLowerInvariant());
                if (!File.Exists(original))
                {
                    File.Copy(audioPath, original);
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
                    // Volta a música removida antes (exclusão lógica).
                    existing.DeletedAt = null;
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
        Path.Combine(paths.Incoming, job.Id + Path.GetExtension(job.OriginalName).ToLowerInvariant());

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
