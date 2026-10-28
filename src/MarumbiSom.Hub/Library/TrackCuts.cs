using System.Globalization;
using System.Text.Json;

namespace MarumbiSom.Hub.Library;

/// <summary>Trecho tirado de uma música (D34), em ms. <see cref="EndMs"/> não entra no trecho.</summary>
public readonly record struct TrackCut(long StartMs, long EndMs)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public long LengthMs => EndMs - StartMs;
}

/// <summary>
/// Conta dos trechos tirados na edição de música (D34). Os cortes guardados ficam no tempo da música enviada (já sem o
/// silêncio do início e do fim, passo 3 da seção 6.5); o editor mostra o tempo da música editada, sem os trechos.
/// </summary>
public static class TrackCuts
{
    /// <summary>Trechos menores que isso são ignorados (um clique sem arrastar).</summary>
    public const long MinCutMs = 100;

    /// <summary>Pedaço mínimo que sobra entre dois trechos; menos que isso vira parte do corte.</summary>
    public const long MinKeptMs = 200;

    /// <summary>Fade de cada lado da emenda, para não estalar.</summary>
    public const double SeamFadeSeconds = 0.02;

    /// <summary>Ordena, limita a <c>[0, duração]</c>, junta os que se encostam e tira os muito curtos.</summary>
    public static IReadOnlyList<TrackCut> Normalize(IEnumerable<TrackCut> cuts, long durationMs)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        var result = new List<TrackCut>();
        foreach (var cut in cuts.Select(c => new TrackCut(Math.Clamp(Math.Min(c.StartMs, c.EndMs), 0, durationMs), Math.Clamp(Math.Max(c.StartMs, c.EndMs), 0, durationMs)))
                     .Where(c => c.LengthMs >= MinCutMs).OrderBy(c => c.StartMs))
        {
            if (result.Count > 0 && cut.StartMs - result[^1].EndMs < MinKeptMs)
            {
                result[^1] = result[^1] with { EndMs = Math.Max(result[^1].EndMs, cut.EndMs) };
            }
            else
            {
                result.Add(cut);
            }
        }

        // Sobras curtas no começo ou no fim também entram no corte.
        if (result.Count > 0 && result[0].StartMs < MinKeptMs)
        {
            result[0] = result[0] with { StartMs = 0 };
        }

        if (result.Count > 0 && durationMs - result[^1].EndMs < MinKeptMs)
        {
            result[^1] = result[^1] with { EndMs = durationMs };
        }

        return result;
    }

    public static long RemovedMs(IReadOnlyList<TrackCut> cuts) => cuts.Sum(c => c.LengthMs);

    /// <summary>Ponto da música editada → ponto da música enviada. Na emenda, vale o ponto depois do trecho tirado.</summary>
    public static long ToSource(long editedMs, IReadOnlyList<TrackCut> cuts)
    {
        var source = editedMs;
        foreach (var cut in cuts)
        {
            if (cut.StartMs > source)
            {
                break;
            }

            source += cut.LengthMs;
        }

        return source;
    }

    /// <summary>Ponto da música enviada → ponto da música editada; um ponto dentro de um trecho tirado vai para a emenda.</summary>
    public static long FromSource(long sourceMs, IReadOnlyList<TrackCut> cuts)
    {
        var removedBefore = 0L;
        foreach (var cut in cuts)
        {
            if (sourceMs < cut.StartMs)
            {
                break;
            }

            if (sourceMs < cut.EndMs)
            {
                return cut.StartMs - removedBefore;
            }

            removedBefore += cut.LengthMs;
        }

        return sourceMs - removedBefore;
    }

    /// <summary>Soma os trechos marcados no editor (tempo da música editada) aos que ela já tinha (tempo da enviada).</summary>
    public static IReadOnlyList<TrackCut> Compose(IReadOnlyList<TrackCut> existing, IEnumerable<TrackCut> editedCuts, long sourceDurationMs)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(editedCuts);
        var mapped = editedCuts.Select(c => new TrackCut(ToSource(c.StartMs, existing), ToSource(c.EndMs, existing)));
        return Normalize([.. existing, .. mapped], sourceDurationMs);
    }

    /// <summary>Pedaços que ficam, na ordem.</summary>
    public static IReadOnlyList<TrackCut> Kept(IReadOnlyList<TrackCut> cuts, long durationMs)
    {
        var kept = new List<TrackCut>();
        var position = 0L;
        foreach (var cut in cuts)
        {
            if (cut.StartMs > position)
            {
                kept.Add(new TrackCut(position, cut.StartMs));
            }

            position = Math.Max(position, cut.EndMs);
        }

        if (position < durationMs)
        {
            kept.Add(new TrackCut(position, durationMs));
        }

        return kept;
    }

    /// <summary>
    /// Filtro do ffmpeg que deixa só os pedaços de <paramref name="kept"/>, com um fade curto em cada emenda.
    /// Entrada <c>[0:a]</c>, saída <c>[out]</c>.
    /// </summary>
    public static string Filter(IReadOnlyList<TrackCut> kept)
    {
        ArgumentNullException.ThrowIfNull(kept);
        if (kept.Count == 0)
        {
            throw new ArgumentException("Nenhum pedaço da música sobra.", nameof(kept));
        }

        var parts = new List<string>();
        var labels = new List<string>();
        parts.Add(kept.Count == 1 ? "[0:a]anull[p0]" : $"[0:a]asplit={kept.Count}" + string.Concat(Enumerable.Range(0, kept.Count).Select(i => $"[p{i}]")));
        for (var i = 0; i < kept.Count; i++)
        {
            var segment = kept[i];
            var chain = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture, $"atrim=start={segment.StartMs / 1000.0:0.###}:end={segment.EndMs / 1000.0:0.###}"),
                "asetpts=PTS-STARTPTS",
            };
            if (i > 0)
            {
                chain.Add(string.Create(CultureInfo.InvariantCulture, $"afade=t=in:st=0:d={SeamFadeSeconds}"));
            }

            if (i < kept.Count - 1)
            {
                var fadeStart = Math.Max(0, (segment.LengthMs / 1000.0) - SeamFadeSeconds);
                chain.Add(string.Create(CultureInfo.InvariantCulture, $"afade=t=out:st={fadeStart:0.###}:d={SeamFadeSeconds}"));
            }

            parts.Add($"[p{i}]" + string.Join(',', chain) + $"[k{i}]");
            labels.Add($"[k{i}]");
        }

        parts.Add(string.Concat(labels) + $"concat=n={kept.Count}:v=0:a=1[out]");
        return string.Join(';', parts);
    }

    public static string? Serialize(IReadOnlyList<TrackCut> cuts) =>
        cuts.Count == 0 ? null : JsonSerializer.Serialize(cuts.Select(c => new[] { c.StartMs, c.EndMs }));

    public static IReadOnlyList<TrackCut> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<long[][]>(json)?.Where(p => p.Length == 2).Select(p => new TrackCut(p[0], p[1])).ToList() ?? [];
    }
}
