using System.Globalization;
using MarumbiSom.Hub.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Hub.Manifests;

/// <summary>
/// Tarefas diárias: regenera os manifestos às 00:05 (seção 6.4, entra e sai oferta da janela de 7 dias), apaga eventos,
/// histórico e execuções de ofertas com mais de 90 dias (seção 6.3) e faz o backup do banco às 02:00 (seção 6.7).
/// </summary>
public sealed class DailyMaintenance(
    ManifestPublisher publisher, IServiceScopeFactory scopes, HubPaths paths, IOptions<HubOptions> options, TimeProvider time, ILogger<DailyMaintenance> logger)
    : BackgroundService
{
    public static readonly TimeOnly BackupTime = new(2, 0);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    public const int BackupsKept = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var zone = ManifestGenerator.ResolveZone(options.Value.Timezone);
        await RunSafelyAsync("regenerar os manifestos na inicialização", () => publisher.RegenerateAllAsync(stoppingToken));
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var regenerate = NextOccurrence(now, options.Value.DailyRegenerationTime, zone);
            var backup = NextOccurrence(now, BackupTime, zone);
            var next = regenerate < backup ? regenerate : backup;
            await Task.Delay(next - now, time, stoppingToken);
            if (next == regenerate)
            {
                await RunSafelyAsync("regenerar os manifestos", () => publisher.RegenerateAllAsync(stoppingToken));
                await RunSafelyAsync("apagar registros antigos", () => PurgeAsync(stoppingToken));
            }
            else
            {
                await RunSafelyAsync("fazer o backup do banco", () => BackupAsync(stoppingToken));
            }
        }
    }

    public static DateTimeOffset NextOccurrence(DateTimeOffset now, TimeOnly at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date.Add(at.ToTimeSpan());
        if (candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }

    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDb>();
        var cutoff = time.GetUtcNow() - Retention;
        var events = await db.Events.Where(e => e.CreatedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        var history = await db.PlayHistory.Where(h => h.StartedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        var plays = await db.OfferPlays.Where(p => p.StartedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        var codes = await db.EnrollCodes.Where(c => c.ExpiresAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        if (events + history + plays + codes > 0)
        {
            logger.LogInformation("Registros com mais de 90 dias apagados: {Events} eventos, {History} músicas tocadas, {Plays} ofertas", events, history, plays);
        }
    }

    /// <summary><c>VACUUM INTO</c> para backups\marumbi-AAAA-MM-DD.db, mantendo os 30 mais recentes.</summary>
    public async Task<string> BackupAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Backups);
        var zone = ManifestGenerator.ResolveZone(options.Value.Timezone);
        var date = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var target = Path.Combine(paths.Backups, $"marumbi-{date}.db");
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDb>();
            await db.Database.ExecuteSqlAsync($"VACUUM INTO {target}", cancellationToken);
        }

        foreach (var old in Directory.EnumerateFiles(paths.Backups, "marumbi-*.db").Order(StringComparer.Ordinal).Reverse().Skip(BackupsKept))
        {
            File.Delete(old);
        }

        logger.LogInformation("Backup do banco em {Path}", target);
        return target;
    }

    private async Task RunSafelyAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha ao {What}", what);
        }
    }
}
