using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Agent.Playback;

/// <summary>Faixa escolhida para tocar. <see cref="PlaylistId"/> e <see cref="ItemId"/> são nulos em "tocar agora".</summary>
public sealed record QueueItem(string? PlaylistId, string? ItemId, string Hash);

/// <summary>Próxima faixa e o cursor que passa a valer quando ela de fato começar.</summary>
public sealed record QueueChoice(QueueItem Item, PlaylistCursorState Cursor);

/// <summary>
/// Ordem de reprodução de cada playlist (seção 10.2, regras 4 a 6). O cursor guarda a última faixa iniciada,
/// então após reiniciar o agente continua da seguinte. Ao mudar o manifesto, a fila é reconstruída a partir
/// da faixa seguinte à atual; se a atual foi removida, recomeça da mesma posição numérica.
/// </summary>
public sealed class PlaylistOrder(Random random)
{
    /// <summary>Calcula a próxima faixa tocável sem alterar o cursor.</summary>
    public QueueChoice? Peek(string playlistId, PlaylistDefinition playlist, PlaylistCursorState? cursor, Func<string, bool> isPlayable)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(isPlayable);
        if (playlist.Items.Count == 0)
        {
            return null;
        }

        return playlist.Shuffle
            ? PeekShuffled(playlistId, playlist, Clone(cursor), isPlayable)
            : PeekSequential(playlistId, playlist, cursor, isPlayable);
    }

    /// <summary>Próximas faixas previstas (para o painel), sem alterar o cursor.</summary>
    public IReadOnlyList<QueueItem> Upcoming(string playlistId, PlaylistDefinition playlist, PlaylistCursorState? cursor, Func<string, bool> isPlayable, int count)
    {
        var result = new List<QueueItem>();
        var current = cursor;
        for (var i = 0; i < count; i++)
        {
            var choice = Peek(playlistId, playlist, current, isPlayable);
            if (choice is null)
            {
                break;
            }

            result.Add(choice.Item);
            current = choice.Cursor;
        }

        return result;
    }

    private static QueueChoice? PeekSequential(string playlistId, PlaylistDefinition playlist, PlaylistCursorState? cursor, Func<string, bool> isPlayable)
    {
        var items = playlist.Items;
        var start = 0;
        if (cursor?.LastItemId is { } last)
        {
            var index = IndexOf(items, last);
            start = index >= 0 ? (index + 1) % items.Count : Math.Clamp(cursor.Index, 0, items.Count - 1);
        }

        for (var offset = 0; offset < items.Count; offset++)
        {
            var position = (start + offset) % items.Count;
            var item = items[position];
            if (isPlayable(item.Hash))
            {
                return new QueueChoice(
                    new QueueItem(playlistId, item.Id, item.Hash),
                    new PlaylistCursorState { Index = position, LastItemId = item.Id });
            }
        }

        return null;
    }

    private QueueChoice? PeekShuffled(string playlistId, PlaylistDefinition playlist, PlaylistCursorState cursor, Func<string, bool> isPlayable)
    {
        var byId = playlist.Items.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Reconcile(cursor, byId.Keys);

        for (var pass = 0; pass < 2; pass++)
        {
            for (var position = cursor.Index + 1; position < cursor.Order.Count; position++)
            {
                var item = byId[cursor.Order[position]];
                if (!isPlayable(item.Hash))
                {
                    continue;
                }

                // A faixa escolhida vem para a frente; as puladas (arquivo ausente) continuam pendentes no ciclo.
                (cursor.Order[cursor.Index + 1], cursor.Order[position]) = (cursor.Order[position], cursor.Order[cursor.Index + 1]);
                cursor.Index++;
                cursor.LastItemId = item.Id;
                return new QueueChoice(new QueueItem(playlistId, item.Id, item.Hash), cursor);
            }

            // Ciclo completo: embaralha de novo, sem repetir a última faixa logo no começo.
            cursor.Order = Shuffle(byId.Keys, cursor.LastItemId);
            cursor.Index = -1;
        }

        return null;
    }

    /// <summary>Ajusta a ordem do ciclo às faixas do manifesto atual: remove as que saíram e sorteia a posição das novas entre as pendentes.</summary>
    private void Reconcile(PlaylistCursorState cursor, IEnumerable<string> ids)
    {
        var valid = ids.ToHashSet(StringComparer.Ordinal);
        var index = Math.Clamp(cursor.Index, -1, cursor.Order.Count - 1);
        var played = cursor.Order.Take(index + 1).Where(valid.Contains).Distinct(StringComparer.Ordinal).ToList();
        var pending = cursor.Order.Skip(index + 1).Where(id => valid.Contains(id) && !played.Contains(id)).Distinct(StringComparer.Ordinal).ToList();
        var known = played.Concat(pending).ToHashSet(StringComparer.Ordinal);
        foreach (var id in valid.Where(id => !known.Contains(id)).Order(StringComparer.Ordinal))
        {
            pending.Insert(random.Next(pending.Count + 1), id);
        }

        cursor.Order = [.. played, .. pending];
        cursor.Index = played.Count - 1;
    }

    private List<string> Shuffle(IEnumerable<string> ids, string? avoidFirst)
    {
        var order = ids.Order(StringComparer.Ordinal).ToList();
        for (var i = order.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        if (order.Count > 1 && order[0] == avoidFirst)
        {
            var swap = 1 + random.Next(order.Count - 1);
            (order[0], order[swap]) = (order[swap], order[0]);
        }

        return order;
    }

    private static int IndexOf(IReadOnlyList<PlaylistItemRef> items, string id)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static PlaylistCursorState Clone(PlaylistCursorState? cursor) => cursor is null
        ? new PlaylistCursorState()
        : new PlaylistCursorState { Order = [.. cursor.Order], Index = cursor.Index, LastItemId = cursor.LastItemId };
}
