using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Hub.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MarumbiSom.Hub.Tests;

/// <summary>Músicas numeradas em ordem de importação (D31).</summary>
public sealed class TrackNumberingTests
{
    [Fact]
    public async Task Migracao_numera_as_musicas_que_ja_existem_e_respeita_as_renomeadas()
    {
        var root = Path.Combine(Path.GetTempPath(), "marumbi-hub-tests", Guid.NewGuid().ToString("N"));
        var paths = new HubPaths(Path.Combine(root, "data"));
        paths.EnsureCreated();
        var options = new DbContextOptionsBuilder<HubDb>().UseSqlite(HubApp.ConnectionString(paths)).Options;
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var converter = new DateTimeOffsetToBinaryConverter();
        long At(int minutes) => (long)converter.ConvertToProvider(start.AddMinutes(minutes))!;
        try
        {
            await using (var db = new HubDb(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261024013513_AtualizacaoAgente");
                // O nome das tags veio igual nas três músicas (o caso do piloto).
                var seed = $$"""
                    INSERT INTO tracks (hash, kind, title, artist, duration_ms, size_bytes, original_name, created_at, deleted_at) VALUES
                    ('h-oferta', 'Offer', 'Picanha R$ 69,90', NULL, 12000, 10, 'picanha.mp3', {{At(0)}}, NULL),
                    ('h-primeira', 'Music', 'Mesmo Nome', 'Banda', 200000, 10, 'a.mp3', {{At(1)}}, NULL),
                    ('h-renomeada', 'Music', 'Forró da Picanha', NULL, 200000, 10, 'c.mp3', {{At(3)}}, NULL),
                    ('h-excluida', 'Music', 'Mesmo Nome', 'Banda', 200000, 10, 'b.mp3', {{At(2)}}, {{At(4)}});
                    INSERT INTO audit_log (action, target, data_json, created_at) VALUES
                    ('tracks.update', 'h-renomeada', {0}, {{At(5)}}),
                    ('tracks.delete', 'h-excluida', {1}, {{At(4)}});
                    """;
                await db.Database.ExecuteSqlRawAsync(seed, "{\"title\":\"Forró da Picanha\",\"artist\":null}", "{\"title\":\"Mesmo Nome\"}");
            }

            await using (var db = new HubDb(options))
            {
                await db.Database.MigrateAsync();
                var tracks = await db.Tracks.AsNoTracking().ToDictionaryAsync(t => t.Hash);
                Assert.Equal((1, "001", null), (tracks["h-primeira"].Number, tracks["h-primeira"].Title, tracks["h-primeira"].Artist));
                Assert.Equal((2, "002"), (tracks["h-excluida"].Number, tracks["h-excluida"].Title));
                Assert.Equal((3, "Forró da Picanha"), (tracks["h-renomeada"].Number, tracks["h-renomeada"].Title));
                Assert.Equal((null, "Picanha R$ 69,90", TrackKind.Offer), (tracks["h-oferta"].Number, tracks["h-oferta"].Title, tracks["h-oferta"].Kind));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Lista_da_biblioteca_vem_pelo_numero()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        foreach (var (title, number) in new[] { ("002", 2), ("Renomeada", 10), ("001", 1) })
        {
            var hash = await hub.SeedTrackAsync(title, artist: "Tag repetida");
            await hub.WithDbAsync(db => db.Tracks.Where(t => t.Hash == hash).ExecuteUpdateAsync(t => t.SetProperty(x => x.Number, number)));
        }

        var list = (await HubHarness.JsonAsync(await admin.GetAsync("/api/tracks"))).EnumerateArray().ToList();
        Assert.Equal([1, 2, 10], list.Select(t => t.GetProperty("number").GetInt32()));
        Assert.Equal(["001", "002", "Renomeada"], list.Select(t => t.GetProperty("title").GetString()));
    }
}
