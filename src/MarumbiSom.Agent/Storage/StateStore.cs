using System.Text.Json;
using MarumbiSom.Contracts.Json;

namespace MarumbiSom.Agent.Storage;

/// <summary>Posição de uma playlist: ordem do ciclo atual (ids de item) e índice da última faixa iniciada.</summary>
public sealed class PlaylistCursorState
{
    public List<string> Order { get; set; } = [];

    public int Index { get; set; } = -1;

    public string? LastItemId { get; set; }
}

/// <summary>Conteúdo de <c>state.json</c> (seção 8.1).</summary>
public sealed class AgentState
{
    public Dictionary<string, PlaylistCursorState> Playlists { get; set; } = [];

    /// <summary>Volume definido pelo comando ao vivo; nulo = volume do manifesto.</summary>
    public int? VolumeOverride { get; set; }

    /// <summary>Volume do manifesto quando o comando foi dado. Se o volume da loja mudar no painel, o comando deixa de valer.</summary>
    public int? VolumeOverrideBase { get; set; }

    /// <summary>Pausa do administrador: vale até "tocar" ou até a próxima janela de agenda começar (seção 10.2).</summary>
    public DateTimeOffset? PausedByAdminAt { get; set; }

    public Dictionary<string, DateTimeOffset> UnreferencedSince { get; set; } = [];

    /// <summary>Músicas tocadas desde o último disparo de cada regra <c>everyTracks</c> (seção 10.3.4).</summary>
    public Dictionary<string, int> SpotTrackCounters { get; set; } = [];

    /// <summary>Último disparo de cada regra <c>everyMinutes</c> e <c>clock</c>.</summary>
    public Dictionary<string, DateTimeOffset> SpotLastTriggered { get; set; } = [];

    /// <summary>Fim da última oferta, base do intervalo mínimo entre ofertas.</summary>
    public DateTimeOffset? LastOfferEndedAt { get; set; }
}

/// <summary>Lê e grava <c>state.json</c> com escrita atômica.</summary>
public sealed class StateStore(AgentDataPaths paths, ILogger<StateStore> logger)
{
    private readonly Lock _lock = new();

    public AgentState State { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(paths.StateFile))
        {
            return;
        }

        try
        {
            State = JsonSerializer.Deserialize<AgentState>(File.ReadAllText(paths.StateFile), ContractJson.Options) ?? new AgentState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogError(ex, "state.json inválido; começando com estado vazio");
            State = new AgentState();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                AtomicFile.WriteAllText(paths.StateFile, JsonSerializer.Serialize(State, ContractJson.Options));
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "Falha ao gravar state.json");
            }
        }
    }
}
