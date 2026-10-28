using MarumbiSom.Hub.Library;

namespace MarumbiSom.Hub.Tests;

/// <summary>Conta dos trechos tirados na edição de música (D34).</summary>
public sealed class TrackCutsTests
{
    [Fact]
    public void Normaliza_ordenando_juntando_e_limitando_a_duracao()
    {
        var cuts = TrackCuts.Normalize(
            [new(30_000, 25_000), new(10_000, 12_000), new(11_000, 15_000), new(15_100, 16_000), new(40_000, 40_050), new(58_000, 70_000)],
            60_000);

        // 10–15 e 15,1–16 se encostam (sobra menor que 200 ms); 40–40,05 é um clique; 58–70 vai até o fim.
        Assert.Equal([new(10_000, 16_000), new(25_000, 30_000), new(58_000, 60_000)], cuts);
    }

    [Fact]
    public void Sobra_curta_no_comeco_ou_no_fim_entra_no_corte()
    {
        Assert.Equal([new(0, 5_000), new(20_000, 30_000)], TrackCuts.Normalize([new(150, 5_000), new(20_000, 29_900)], 30_000));
    }

    [Fact]
    public void Ponto_da_musica_editada_vai_para_o_tempo_da_enviada_e_volta()
    {
        IReadOnlyList<TrackCut> cuts = [new(10_000, 20_000), new(30_000, 40_000)];

        Assert.Equal(5_000, TrackCuts.ToSource(5_000, cuts));
        Assert.Equal(25_000, TrackCuts.ToSource(15_000, cuts));
        Assert.Equal(40_000, TrackCuts.ToSource(20_000, cuts));
        Assert.Equal(45_000, TrackCuts.ToSource(25_000, cuts));

        Assert.Equal(15_000, TrackCuts.FromSource(25_000, cuts));
        Assert.Equal(25_000, TrackCuts.FromSource(45_000, cuts));
        // Dentro de um trecho tirado: vai para a emenda.
        Assert.Equal(10_000, TrackCuts.FromSource(12_000, cuts));
        Assert.Equal(20_000, TrackCuts.FromSource(35_000, cuts));
    }

    [Fact]
    public void Editar_de_novo_soma_os_cortes_no_tempo_da_enviada()
    {
        IReadOnlyList<TrackCut> existing = [new(0, 5_000)];

        // Na música editada (sem os 5 s do começo), tirar 10–20 s é tirar 15–25 s da enviada.
        Assert.Equal([new(0, 5_000), new(15_000, 25_000)], TrackCuts.Compose(existing, [new(10_000, 20_000)], 60_000));
        // 0–15 s da editada passa por cima do corte antigo (10–20 s da enviada) e vira 0–25 s.
        Assert.Equal([new(0, 25_000)], TrackCuts.Compose([new(10_000, 20_000)], [new(0, 15_000)], 60_000));
    }

    [Fact]
    public void Pedacos_que_ficam_e_filtro_do_ffmpeg_com_fade_nas_emendas()
    {
        var kept = TrackCuts.Kept([new(0, 5_000), new(15_000, 25_000)], 60_000);

        Assert.Equal([new(5_000, 15_000), new(25_000, 60_000)], kept);
        Assert.Equal(
            "[0:a]asplit=2[p0][p1];"
            + "[p0]atrim=start=5:end=15,asetpts=PTS-STARTPTS,afade=t=out:st=9.98:d=0.02[k0];"
            + "[p1]atrim=start=25:end=60,asetpts=PTS-STARTPTS,afade=t=in:st=0:d=0.02[k1];"
            + "[k0][k1]concat=n=2:v=0:a=1[out]",
            TrackCuts.Filter(kept));
        Assert.Equal("[0:a]anull[p0];[p0]atrim=start=0:end=50,asetpts=PTS-STARTPTS[k0];[k0]concat=n=1:v=0:a=1[out]", TrackCuts.Filter([new(0, 50_000)]));
    }

    [Fact]
    public void Cortes_guardados_em_json()
    {
        IReadOnlyList<TrackCut> cuts = [new(0, 5_000), new(15_000, 25_000)];

        Assert.Equal("[[0,5000],[15000,25000]]", TrackCuts.Serialize(cuts));
        Assert.Equal(cuts, TrackCuts.Parse(TrackCuts.Serialize(cuts)));
        Assert.Null(TrackCuts.Serialize([]));
        Assert.Empty(TrackCuts.Parse(null));
    }
}
