namespace MarumbiSom.Agent.Windows.Abstractions;

public enum AudioEndpointState
{
    Active,
    Disabled,
    NotPresent,
    Unplugged,
}

public sealed record AudioEndpoint(string Id, string FriendlyName, Guid? ContainerId, AudioEndpointState State);

/// <summary>Endpoints de saída de áudio do Windows (Core Audio).</summary>
public interface IAudioEndpoints
{
    /// <summary>Todos os endpoints de saída, em qualquer estado.</summary>
    IReadOnlyList<AudioEndpoint> ListRenderEndpoints();

    string? GetDefaultRenderEndpointId();

    /// <summary>Define a saída padrão (todas as funções) via <c>IPolicyConfig</c>, interface não documentada.</summary>
    void SetDefaultRenderEndpoint(string endpointId);

    /// <summary>Volume do endpoint, 0,0–1,0, e retira o mudo.</summary>
    void SetEndpointVolume(string endpointId, float scalar);
}
