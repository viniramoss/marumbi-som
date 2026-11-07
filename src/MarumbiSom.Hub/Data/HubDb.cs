using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MarumbiSom.Hub.Data;

/// <summary>Banco SQLite do Hub (seção 6.3), em modo WAL.</summary>
public sealed class HubDb(DbContextOptions<HubDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Track> Tracks => Set<Track>();

    public DbSet<Playlist> Playlists => Set<Playlist>();

    public DbSet<PlaylistItem> PlaylistItems => Set<PlaylistItem>();

    public DbSet<Store> Stores => Set<Store>();

    public DbSet<Schedule> Schedules => Set<Schedule>();

    public DbSet<StoreManifest> StoreManifests => Set<StoreManifest>();

    public DbSet<StoreState> StoreStates => Set<StoreState>();

    public DbSet<EnrollCode> EnrollCodes => Set<EnrollCode>();

    public DbSet<StoreCommand> Commands => Set<StoreCommand>();

    public DbSet<StoreEvent> Events => Set<StoreEvent>();

    public DbSet<PlayHistory> PlayHistory => Set<PlayHistory>();

    public DbSet<Offer> Offers => Set<Offer>();

    public DbSet<OfferStore> OfferStores => Set<OfferStore>();

    public DbSet<OfferPlacement> OfferPlacements => Set<OfferPlacement>();

    public DbSet<OfferPlay> OfferPlays => Set<OfferPlay>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<AgentRelease> AgentReleases => Set<AgentRelease>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        // O SQLite não compara DateTimeOffset; como inteiro (ticks UTC) ordenação e filtros funcionam no banco.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<Contracts.Manifests.TrackKind>().HaveConversion<string>();
        configurationBuilder.Properties<Contracts.Manifests.SpotMode>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.Username).IsUnique();
        });
        modelBuilder.Entity<Track>(e =>
        {
            e.ToTable("tracks");
            e.HasKey(t => t.Hash);
            e.HasIndex(t => t.Kind);
        });
        modelBuilder.Entity<Playlist>(e =>
        {
            e.ToTable("playlists");
            e.HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PlaylistId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PlaylistItem>(e =>
        {
            e.ToTable("playlist_items");
            e.HasIndex(i => new { i.PlaylistId, i.Position }).IsUnique();
            e.HasIndex(i => i.TrackHash);
            e.HasOne<Track>().WithMany().HasForeignKey(i => i.TrackHash).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Store>(e =>
        {
            e.ToTable("stores");
            e.HasIndex(s => s.Slug).IsUnique();
            e.HasIndex(s => s.TokenHash).IsUnique();
            e.HasMany(s => s.Schedules).WithOne().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Schedule>(e =>
        {
            e.ToTable("schedules");
            e.HasOne<Playlist>().WithMany().HasForeignKey(x => x.PlaylistId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<StoreManifest>(e =>
        {
            e.ToTable("store_manifests");
            e.HasKey(m => new { m.StoreId, m.Version });
            e.HasOne<Store>().WithMany().HasForeignKey(m => m.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StoreState>(e =>
        {
            e.ToTable("store_state");
            e.HasKey(s => s.StoreId);
            e.HasOne<Store>().WithOne().HasForeignKey<StoreState>(s => s.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<EnrollCode>(e =>
        {
            e.ToTable("enroll_codes");
            e.HasKey(c => c.CodeHash);
            e.HasOne<Store>().WithMany().HasForeignKey(c => c.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StoreCommand>(e =>
        {
            e.ToTable("commands");
            e.HasIndex(c => new { c.StoreId, c.CreatedAt });
            e.HasOne<Store>().WithMany().HasForeignKey(c => c.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StoreEvent>(e =>
        {
            e.ToTable("events");
            e.HasIndex(x => new { x.StoreId, x.CreatedAt });
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<PlayHistory>(e =>
        {
            e.ToTable("play_history");
            e.HasIndex(x => new { x.StoreId, x.StartedAt });
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<Offer>(e =>
        {
            e.ToTable("offers");
            e.HasOne<Track>().WithMany().HasForeignKey(o => o.AudioHash).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<OfferStore>(e =>
        {
            e.ToTable("offer_stores");
            e.HasKey(x => new { x.OfferId, x.StoreId });
            e.HasOne<Offer>().WithMany().HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<OfferPlacement>(e =>
        {
            e.ToTable("offer_placements");
            e.HasOne<Offer>().WithMany().HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<OfferPlay>(e =>
        {
            e.ToTable("offer_plays");
            e.HasIndex(x => new { x.StoreId, x.StartedAt });
            e.HasIndex(x => x.OfferId);
        });
        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasIndex(x => x.CreatedAt);
        });
        modelBuilder.Entity<Setting>(e =>
        {
            e.ToTable("settings");
            e.HasKey(x => x.Key);
        });
        modelBuilder.Entity<AgentRelease>(e =>
        {
            e.ToTable("agent_releases");
            e.HasKey(r => r.Version);
        });

        // Colunas em snake_case, como na seção 6.3.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(SnakeCase(property.Name));
            }
        }
    }

    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
