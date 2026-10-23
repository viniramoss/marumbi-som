using System.Text.Json;
using System.Text.Json.Serialization;
using MarumbiSom.Contracts.Json;

namespace MarumbiSom.Hub;

/// <summary>JSON da API do painel: as mesmas regras dos contratos (camelCase, enums em texto, horários "HH:mm").</summary>
public static class HubJson
{
    public static JsonSerializerOptions Options { get; } = CreateReadOnly();

    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new HhMmTimeOnlyConverter());
    }

    private static JsonSerializerOptions CreateReadOnly()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
