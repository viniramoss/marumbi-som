using System.Text.Json;

namespace MarumbiSom.Agent.Player;

/// <summary>Evento recebido do mpv pelo IPC JSON (ex.: <c>start-file</c>, <c>end-file</c>).</summary>
public sealed class MpvEventArgs(string name, JsonElement raw) : EventArgs
{
    public string Name { get; } = name;

    public JsonElement Raw { get; } = raw;

    /// <summary>Motivo do <c>end-file</c>: eof, stop, quit, error, redirect.</summary>
    public string? Reason => Raw.TryGetProperty("reason", out var r) ? r.GetString() : null;
}

/// <summary>Um processo mpv controlado por IPC (seção 10.1). O agente usa dois: música e ofertas.</summary>
public interface IMpvInstance : IAsyncDisposable
{
    string Name { get; }

    bool IsRunning { get; }

    event EventHandler<MpvEventArgs>? EventReceived;

    /// <summary>Disparado quando o processo termina sem ter sido parado pelo agente.</summary>
    event EventHandler? Exited;

    /// <summary>Inicia o processo com a saída na caixa (<c>--audio-device=wasapi/{id}</c>).</summary>
    Task StartAsync(string audioEndpointId, double volume, CancellationToken cancellationToken);

    Task StopAsync();

    Task<JsonElement> CommandAsync(IReadOnlyList<object> command, CancellationToken cancellationToken);
}

public static class MpvInstanceExtensions
{
    public static Task LoadFileAsync(this IMpvInstance mpv, string path, bool append, CancellationToken cancellationToken) =>
        mpv.CommandAsync(["loadfile", path, append ? "append" : "replace"], cancellationToken);

    public static Task SetPropertyAsync(this IMpvInstance mpv, string name, object value, CancellationToken cancellationToken) =>
        mpv.CommandAsync(["set_property", name, value], cancellationToken);

    public static Task<JsonElement> GetPropertyAsync(this IMpvInstance mpv, string name, CancellationToken cancellationToken) =>
        mpv.CommandAsync(["get_property", name], cancellationToken);

    public static Task SetVolumeAsync(this IMpvInstance mpv, double volume, CancellationToken cancellationToken) =>
        mpv.SetPropertyAsync("volume", Math.Clamp(Math.Round(volume, 2), 0, 100), cancellationToken);

    public static Task SetPausedAsync(this IMpvInstance mpv, bool paused, CancellationToken cancellationToken) =>
        mpv.SetPropertyAsync("pause", paused, cancellationToken);

    /// <summary>Posição atual em segundos, ou <c>null</c> se nada estiver carregado.</summary>
    public static async Task<double?> GetPositionAsync(this IMpvInstance mpv, CancellationToken cancellationToken)
    {
        try
        {
            var value = await mpv.GetPropertyAsync("time-pos", cancellationToken);
            return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        }
        catch (MpvCommandException)
        {
            // "property unavailable" quando o mpv está ocioso.
            return null;
        }
    }

    public static Task SeekAsync(this IMpvInstance mpv, double seconds, CancellationToken cancellationToken) =>
        mpv.CommandAsync(["seek", seconds, "absolute"], cancellationToken);

    /// <summary>Reabre a saída de áudio na caixa, usado quando ela reconecta.</summary>
    public static async Task ReloadAudioOutputAsync(this IMpvInstance mpv, string audioEndpointId, CancellationToken cancellationToken)
    {
        await mpv.SetPropertyAsync("audio-device", "wasapi/" + audioEndpointId, cancellationToken);
        await mpv.CommandAsync(["ao-reload"], cancellationToken);
    }
}

public sealed class MpvCommandException : Exception
{
    public MpvCommandException()
    {
    }

    public MpvCommandException(string message)
        : base(message)
    {
    }

    public MpvCommandException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
