using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Agent.Playback;

/// <summary>Destino dos eventos do player (track.started, spot.*, eventos do agente). Na Etapa 3 o HubClient os envia ao Hub.</summary>
public interface IPlaybackEvents
{
    event EventHandler<AgentMessage>? Published;

    void Publish(AgentMessage message);

    /// <summary>Últimos eventos, do mais antigo para o mais recente (status da configuração local).</summary>
    IReadOnlyList<(DateTimeOffset At, AgentMessage Message)> Recent { get; }
}

public sealed class PlaybackEventLog(TimeProvider time, ILogger<PlaybackEventLog> logger) : IPlaybackEvents
{
    private const int Capacity = 200;
    private readonly Lock _lock = new();
    private readonly Queue<(DateTimeOffset, AgentMessage)> _recent = new();

    public event EventHandler<AgentMessage>? Published;

    public IReadOnlyList<(DateTimeOffset At, AgentMessage Message)> Recent
    {
        get
        {
            lock (_lock)
            {
                return _recent.ToList();
            }
        }
    }

    public void Publish(AgentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_lock)
        {
            _recent.Enqueue((time.GetUtcNow(), message));
            while (_recent.Count > Capacity)
            {
                _recent.Dequeue();
            }
        }

        logger.LogDebug("Evento: {Message}", message);
        try
        {
            Published?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao repassar o evento {Message}", message);
        }
    }
}
