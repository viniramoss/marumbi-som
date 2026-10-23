using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Agents;
using MarumbiSom.Hub.Data;
using Microsoft.EntityFrameworkCore;

namespace MarumbiSom.Hub.Manifests;

public sealed record PublishedManifest(int StoreId, string Slug, long Version);

/// <summary>
/// Regenera os manifestos (seção 6.4). A versão só sobe quando o <c>contentHash</c> muda e nunca diminui; ficam as
/// últimas 20 versões. Lojas conectadas recebem <c>manifest.changed</c>.
/// </summary>
public sealed class ManifestPublisher(IServiceScopeFactory scopes, AgentConnections agents, TimeProvider time, ILogger<ManifestPublisher> logger) : IDisposable
{
    public const int KeptVersions = 20;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Disparado para cada loja cuja versão subiu (o painel mostra "Baixando" até a loja aplicar).</summary>
    public event EventHandler<PublishedManifest>? Published;

    public void Dispose() => _lock.Dispose();

    /// <summary>Regenera todas as lojas. Uma loja cujo conteúdo não mudou mantém a versão.</summary>
    public async Task<IReadOnlyList<PublishedManifest>> RegenerateAllAsync(CancellationToken cancellationToken)
    {
        var published = new List<PublishedManifest>();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDb>();
            var snapshot = await LoadSnapshotAsync(db, cancellationToken);
            var now = time.GetUtcNow();
            var stores = await db.Stores.Include(s => s.Schedules).AsNoTracking().ToListAsync(cancellationToken);
            foreach (var store in stores)
            {
                var manifest = ManifestGenerator.Build(store, snapshot, now);
                var latest = await db.StoreManifests.Where(m => m.StoreId == store.Id)
                    .OrderByDescending(m => m.Version).Select(m => new { m.Version, m.ContentHash }).FirstOrDefaultAsync(cancellationToken);
                if (latest?.ContentHash == manifest.ContentHash)
                {
                    continue;
                }

                var version = (latest?.Version ?? 0) + 1;
                manifest = manifest with { Version = version };
                db.StoreManifests.Add(new StoreManifest
                {
                    StoreId = store.Id,
                    Version = version,
                    ContentHash = manifest.ContentHash!,
                    Json = JsonSerializer.Serialize(manifest, ContractJson.Options),
                    CreatedAt = now,
                });
                await db.StoreManifests.Where(m => m.StoreId == store.Id && m.Version <= version - KeptVersions).ExecuteDeleteAsync(cancellationToken);
                published.Add(new PublishedManifest(store.Id, store.Slug, version));
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        foreach (var item in published)
        {
            logger.LogInformation("Manifesto da loja {Store} agora na versão {Version}", item.Slug, item.Version);
            await agents.NotifyManifestChangedAsync(item.StoreId, item.Version, cancellationToken);
            Published?.Invoke(this, item);
        }

        return published;
    }

    public static async Task<HubSnapshot> LoadSnapshotAsync(HubDb db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var playlists = await db.Playlists.Include(p => p.Items).AsNoTracking().ToDictionaryAsync(p => p.Id, cancellationToken);
        var tracks = await db.Tracks.Where(t => t.DeletedAt == null).AsNoTracking().ToDictionaryAsync(t => t.Hash, StringComparer.Ordinal, cancellationToken);
        var offers = await db.Offers.AsNoTracking().ToListAsync(cancellationToken);
        var offerStores = (await db.OfferStores.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(o => o.OfferId, o => o.StoreId);
        var placements = (await db.OfferPlacements.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(p => p.OfferId);
        return new HubSnapshot(playlists, tracks, offers, offerStores, placements);
    }

    /// <summary>Manifesto mais recente da loja, já serializado.</summary>
    public static Task<StoreManifest?> LatestAsync(HubDb db, int storeId, CancellationToken cancellationToken) =>
        db.StoreManifests.AsNoTracking().Where(m => m.StoreId == storeId).OrderByDescending(m => m.Version).FirstOrDefaultAsync(cancellationToken);

    public static Manifest Deserialize(StoreManifest stored) =>
        JsonSerializer.Deserialize<Manifest>(stored?.Json ?? throw new ArgumentNullException(nameof(stored)), ContractJson.Options)
        ?? throw new InvalidOperationException("Manifesto vazio no banco.");
}
