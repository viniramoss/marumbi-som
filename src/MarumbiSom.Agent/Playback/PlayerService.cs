using MarumbiSom.Agent.Storage;

namespace MarumbiSom.Agent.Playback;

/// <summary>Laço do player: chama <see cref="PlayerEngine.StepAsync"/> e confere a cada 5 s se o manifesto ativo mudou no disco.</summary>
public sealed class PlayerService(PlayerEngine engine, ManifestStore manifests, TimeProvider time, ILogger<PlayerService> logger) : BackgroundService
{
    private static readonly TimeSpan ManifestCheckInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastManifestCheck = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var now = time.GetUtcNow();
                if (now - lastManifestCheck >= ManifestCheckInterval)
                {
                    lastManifestCheck = now;
                    manifests.ReloadIfChangedOnDisk();
                }

                delay = await engine.StepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha no ciclo do player");
                delay = TimeSpan.FromSeconds(1);
            }

            try
            {
                await Task.Delay(delay, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await engine.StopAsync();
    }
}
