using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Contracts.Tests;

public class ManifestSerializationTests
{
    internal static Manifest LoadSample() =>
        JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "manifest-loja-2.json")), ContractJson.Options)!;

    [Fact]
    public void Desserializa_o_manifesto_de_exemplo_da_secao_11_1()
    {
        var manifest = LoadSample();

        Assert.Equal("loja-2", manifest.StoreId);
        Assert.Equal(47, manifest.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 16, 20, 0, TimeSpan.FromHours(-3)), manifest.GeneratedAt);
        Assert.Equal(70, manifest.Volume);
        Assert.Equal(5, manifest.CrossfadeS);
        Assert.Equal(2, manifest.Schedules.Count);
        Assert.Equal(new TimeOnly(8, 0), manifest.Schedules[0].Start);
        Assert.Equal([1, 2, 3, 4, 5, 6], manifest.Schedules[0].Days);
        Assert.True(manifest.Playlists["manha"].Shuffle);
        Assert.Equal("it-88", manifest.Playlists["tarde"].Items[0].Id);
        Assert.Equal(4, manifest.Tracks.Count);
        Assert.Equal(TrackKind.Offer, manifest.Tracks.Values.Single(t => t.Title.StartsWith("Picanha", StringComparison.Ordinal)).Kind);
        Assert.Equal(300, manifest.Spots.MinOfferGapS);
        Assert.Equal(30, manifest.Spots.Offers["of-12"].DuckLevel);
    }

    [Fact]
    public void Posicionamentos_sao_desserializados_pelo_campo_kind_mesmo_fora_da_primeira_posicao()
    {
        var placements = LoadSample().Spots.Placements;

        var between = Assert.IsType<AnchorBetweenPlacement>(placements[0]);
        Assert.Equal("it-88", between.AfterItemId);

        var during = Assert.IsType<AnchorDuringPlacement>(placements[1]);
        Assert.Equal(80000, during.OffsetMs);

        var everyMinutes = Assert.IsType<EveryMinutesPlacement>(placements[2]);
        Assert.Equal(15, everyMinutes.Minutes);
        Assert.Equal(SpotMode.During, everyMinutes.Mode);

        var clock = Assert.IsType<ClockPlacement>(placements[3]);
        Assert.Equal(new TimeOnly(12, 0), clock.Time);

        var everyTracks = Assert.IsType<EveryTracksPlacement>(placements[4]);
        Assert.Equal(4, everyTracks.N);
        Assert.Equal(["tarde"], everyTracks.PlaylistIds!);
    }

    [Fact]
    public void Serializacao_usa_camelCase_e_valores_textuais()
    {
        var json = JsonSerializer.Serialize(LoadSample(), ContractJson.Options);

        Assert.Contains("\"storeId\":\"loja-2\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"anchorDuring\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mode\":\"during\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"offer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"start\":\"08:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"n\":4", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Ida_e_volta_preserva_o_conteudo()
    {
        var original = LoadSample();

        var json = JsonSerializer.Serialize(original, ContractJson.Options);
        var back = JsonSerializer.Deserialize<Manifest>(json, ContractJson.Options)!;

        Assert.Equal(ManifestHasher.ComputeContentHash(original), ManifestHasher.ComputeContentHash(back));
        Assert.Equal(json, JsonSerializer.Serialize(back, ContractJson.Options));
    }

    [Theory]
    [InlineData("\"00:00\"", 0, 0)]
    [InlineData("\"23:59\"", 23, 59)]
    public void Horario_HH_mm_e_aceito(string json, int hour, int minute)
    {
        Assert.Equal(new TimeOnly(hour, minute), JsonSerializer.Deserialize<TimeOnly>(json, ContractJson.Options));
    }

    [Fact]
    public void Horario_24_00_representa_o_fim_do_dia()
    {
        var value = JsonSerializer.Deserialize<TimeOnly>("\"24:00\"", ContractJson.Options);

        Assert.Equal(TimeOnly.MaxValue, value);
        Assert.Equal("\"24:00\"", JsonSerializer.Serialize(value, ContractJson.Options));
    }

    [Theory]
    [InlineData("\"8:00\"")]
    [InlineData("\"08:00:00\"")]
    [InlineData("\"25:00\"")]
    public void Horario_invalido_e_rejeitado(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TimeOnly>(json, ContractJson.Options));
    }

    [Fact]
    public void Kind_desconhecido_e_rejeitado()
    {
        const string json = """{ "id": "pl-9", "offerId": "of-1", "kind": "sorteio" }""";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Placement>(json, ContractJson.Options));
    }

    [Fact]
    public void Validade_da_oferta_inclui_inicio_e_exclui_fim()
    {
        var offer = LoadSample().Spots.Offers["of-12"];

        Assert.False(offer.IsValidAt(offer.ValidFrom.AddSeconds(-1)));
        Assert.True(offer.IsValidAt(offer.ValidFrom));
        Assert.True(offer.IsValidAt(offer.ValidUntil.AddSeconds(-1)));
        Assert.False(offer.IsValidAt(offer.ValidUntil));
    }
}
