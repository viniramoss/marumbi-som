using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarumbiSom.Agent.Storage;

namespace MarumbiSom.Agent.LocalConfig;

/// <summary>
/// Edita <c>config.json</c> (seção 8.1) preservando as demais chaves. Gravação atômica (regra 12); o arquivo
/// fica em C:\ProgramData\MarumbiSom, legível e gravável só por SYSTEM e Administradores.
/// </summary>
public sealed class LocalConfigFile(AgentDataPaths paths)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Lock _lock = new();

    public string? Get(string section, string key)
    {
        lock (_lock)
        {
            return Read()[section]?[key]?.GetValue<string>();
        }
    }

    public void Set(string section, string key, string? value)
    {
        lock (_lock)
        {
            var root = Read();
            if (root[section] is not JsonObject node)
            {
                node = [];
                root[section] = node;
            }

            node[key] = value;
            AtomicFile.WriteAllText(paths.ConfigFile, root.ToJsonString(Indented));
        }
    }

    private JsonObject Read()
    {
        if (!File.Exists(paths.ConfigFile))
        {
            return [];
        }

        // File.ReadAllText descarta o BOM que o PowerShell 5.1 grava em UTF-8.
        var text = File.ReadAllText(paths.ConfigFile);
        return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
    }
}
