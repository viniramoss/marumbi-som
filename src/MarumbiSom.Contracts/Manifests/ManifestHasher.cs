using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MarumbiSom.Contracts.Json;

namespace MarumbiSom.Contracts.Manifests;

/// <summary>
/// Calcula o <c>contentHash</c> do manifesto: SHA-256 do JSON canônico (chaves em ordem ordinal,
/// sem espaços), sem os campos <c>version</c>, <c>contentHash</c> e <c>generatedAt</c> (seção 6.4 e decisão D13).
/// </summary>
public static class ManifestHasher
{
    public const string Prefix = "sha256:";

    private static readonly string[] ExcludedFields = ["version", "contentHash", "generatedAt"];

    public static string ComputeContentHash(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var node = JsonSerializer.SerializeToNode(manifest, ContractJson.Options)?.AsObject()
            ?? throw new InvalidOperationException("Manifesto serializou para nulo.");
        foreach (var field in ExcludedFields)
        {
            node.Remove(field);
        }

        var canonical = ToCanonicalJson(node);
        var hash = SHA256.HashData(canonical);
        return Prefix + Convert.ToHexStringLower(hash);
    }

    public static Manifest WithContentHash(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest with { ContentHash = ComputeContentHash(manifest) };
    }

    /// <summary>Serializa um nó JSON em forma canônica (UTF-8, chaves ordenadas, sem espaços).</summary>
    public static byte[] ToCanonicalJson(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, Encoder = ContractJson.Options.Encoder }))
        {
            WriteCanonical(writer, node);
        }

        return buffer.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteCanonical(writer, value);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}
