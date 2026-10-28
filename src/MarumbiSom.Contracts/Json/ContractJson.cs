using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarumbiSom.Contracts.Json;

/// <summary>
/// Opções de serialização compartilhadas por Hub e agente (seção 11): JSON em camelCase,
/// enums como texto em camelCase, datas ISO 8601 com fuso e horários "HH:mm".
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // O campo "kind"/"type" pode vir em qualquer posição do objeto (ex.: manifesto da seção 11.1).
            AllowOutOfOrderMetadataProperties = true,
            Encoder = JavaScriptEncoder.Default,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new HhMmTimeOnlyConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
