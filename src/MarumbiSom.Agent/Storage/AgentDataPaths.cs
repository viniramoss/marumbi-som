using MarumbiSom.Agent.Configuration;

namespace MarumbiSom.Agent.Storage;

/// <summary>Estrutura de C:\ProgramData\MarumbiSom (seção 8.1). A raiz é injetável para testes e simulação.</summary>
public sealed class AgentDataPaths(string root)
{
    public static AgentDataPaths Default { get; } = new(AgentPaths.DataDirectory);

    public string Root { get; } = root;

    public string ManifestsDirectory => Path.Combine(Root, "manifests");

    public string ActiveManifest => Path.Combine(ManifestsDirectory, "active.json");

    public string PendingManifest => Path.Combine(ManifestsDirectory, "pending.json");

    public string CacheDirectory => Path.Combine(Root, "cache");

    public string PartialDirectory => Path.Combine(CacheDirectory, ".part");

    public string StateFile => Path.Combine(Root, "state.json");

    public string ConfigFile => Path.Combine(Root, "config.json");

    public string LogsDirectory => Path.Combine(Root, "logs");
}
