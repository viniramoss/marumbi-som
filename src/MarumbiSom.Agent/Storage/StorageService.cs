namespace MarumbiSom.Agent.Storage;

/// <summary>
/// Carrega estado e manifestos antes do player começar, confere se os arquivos do manifesto ativo existem
/// (seção 8.3) e faz a limpeza do cache a cada 6 horas (seção 8.2).
/// </summary>
public sealed class StorageService(
    AgentDataPaths paths,
    ManifestStore manifests,
    StateStore state,
    CacheManager cache,
    TimeProvider time,
    ILogger<StorageService> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.ManifestsDirectory);
        Directory.CreateDirectory(paths.CacheDirectory);
        state.Load();
        manifests.Load();
        if (manifests.Active is { } active)
        {
            var missing = cache.Missing(active);
            if (missing.Count > 0)
            {
                // Sem o Hub (Etapa 2) não há como baixar de novo; as faixas ausentes são puladas.
                logger.LogWarning("{Count} arquivo(s) do manifesto ativo ausentes no cache; serão pulados: {Hashes}",
                    missing.Count, string.Join(", ", missing.Take(10)));
            }
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
                Cleanup();
                await Task.Delay(CleanupInterval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "Falha na limpeza do cache");
            }
        }
    }

    public void Cleanup()
    {
        // Regra 4: nunca apagar arquivo referenciado pelo manifesto ativo ou pendente.
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in new[] { manifests.Active, manifests.Pending })
        {
            if (manifest is not null)
            {
                referenced.UnionWith(manifest.Tracks.Keys);
            }
        }

        if (manifests.Active is null)
        {
            logger.LogInformation("Limpeza do cache adiada: nenhum manifesto ativo");
            return;
        }

        var removed = cache.Cleanup(referenced, state.State.UnreferencedSince);
        state.Save();
        if (removed > 0)
        {
            logger.LogInformation("Limpeza do cache removeu {Count} arquivo(s)", removed);
        }
    }
}
