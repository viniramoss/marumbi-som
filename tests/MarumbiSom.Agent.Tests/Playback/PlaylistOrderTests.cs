using MarumbiSom.Agent.Playback;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Agent.Tests.Playback;

public class PlaylistOrderTests
{
    private readonly PlaylistOrder _order = new(new Random(42));

    private static PlaylistDefinition Playlist(bool shuffle, params string[] ids) =>
        new(shuffle, ids.Select(id => new PlaylistItemRef(id, "h-" + id)).ToList());

    private static bool All(string hash) => true;

    private List<string> Play(PlaylistDefinition playlist, int count, ref PlaylistCursorState? cursor, Func<string, bool>? playable = null)
    {
        var played = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var choice = _order.Peek("p", playlist, cursor, playable ?? All)!;
            played.Add(choice.Item.ItemId!);
            cursor = choice.Cursor;
        }

        return played;
    }

    [Fact]
    public void Sequencial_segue_a_posicao_e_recomeca_no_fim()
    {
        PlaylistCursorState? cursor = null;

        Assert.Equal(["a", "b", "c", "a"], Play(Playlist(false, "a", "b", "c"), 4, ref cursor));
    }

    [Fact]
    public void Peek_nao_altera_o_cursor()
    {
        var cursor = new PlaylistCursorState { Index = 0, LastItemId = "a" };

        _order.Peek("p", Playlist(false, "a", "b"), cursor, All);

        Assert.Equal("a", cursor.LastItemId);
    }

    [Fact]
    public void Continua_da_seguinte_a_ultima_iniciada()
    {
        PlaylistCursorState? cursor = new() { Index = 1, LastItemId = "b" };

        Assert.Equal(["c"], Play(Playlist(false, "a", "b", "c"), 1, ref cursor));
    }

    [Fact]
    public void Nova_ordem_segue_a_partir_da_faixa_atual()
    {
        PlaylistCursorState? cursor = new() { Index = 0, LastItemId = "a" };

        Assert.Equal(["b", "c"], Play(Playlist(false, "c", "a", "b"), 2, ref cursor));
    }

    [Fact]
    public void Faixa_atual_removida_recomeca_na_mesma_posicao_numerica()
    {
        PlaylistCursorState? cursor = new() { Index = 1, LastItemId = "b" };
        Assert.Equal(["c"], Play(Playlist(false, "a", "c", "d"), 1, ref cursor));

        PlaylistCursorState? atEnd = new() { Index = 5, LastItemId = "z" };
        Assert.Equal(["c"], Play(Playlist(false, "a", "b", "c"), 1, ref atEnd));
    }

    [Fact]
    public void Pula_faixas_sem_arquivo()
    {
        PlaylistCursorState? cursor = null;

        Assert.Equal(["a", "c", "a"], Play(Playlist(false, "a", "b", "c"), 3, ref cursor, h => h != "h-b"));
        Assert.Null(_order.Peek("p", Playlist(false, "a"), null, _ => false));
    }

    [Fact]
    public void Aleatorio_nao_repete_ate_passar_por_todas()
    {
        var playlist = Playlist(true, "a", "b", "c", "d", "e", "f");
        PlaylistCursorState? cursor = null;

        var played = Play(playlist, 18, ref cursor);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.Equal(6, played.Skip(cycle * 6).Take(6).Distinct().Count());
        }

        // Nunca a mesma faixa duas vezes seguidas, nem na virada do ciclo.
        Assert.All(played.Zip(played.Skip(1)), p => Assert.NotEqual(p.First, p.Second));
    }

    [Fact]
    public void Aleatorio_mantem_o_ciclo_quando_o_manifesto_muda()
    {
        PlaylistCursorState? cursor = null;
        var first = Play(Playlist(true, "a", "b", "c", "d"), 2, ref cursor);

        // Remove uma já tocada e uma pendente, adiciona duas novas: as pendentes + novas tocam antes de repetir.
        var changed = Playlist(true, first[0], "e", "f").Items.Concat(Playlist(true, "a", "b", "c", "d").Items
            .Where(i => i.Id != first[0] && i.Id != first[1]).Take(1)).ToList();
        var playlist = new PlaylistDefinition(true, changed);
        var rest = Play(playlist, 3, ref cursor);

        Assert.DoesNotContain(first[0], rest);
        Assert.Equal(3, rest.Distinct().Count());
    }

    [Fact]
    public void Proximas_faixas_previstas()
    {
        var upcoming = _order.Upcoming("p", Playlist(false, "a", "b", "c"), new PlaylistCursorState { Index = 0, LastItemId = "a" }, All, 4);

        Assert.Equal(["b", "c", "a", "b"], upcoming.Select(i => i.ItemId));
    }
}
