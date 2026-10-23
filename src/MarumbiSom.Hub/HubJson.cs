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
        // Campos nulos vão como null (não somem): o OpenAPI declara todos os campos das respostas como presentes (D23 g).
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        // Números só como número, sem aceitar "12" em texto; assim o OpenAPI descreve inteiros como inteiros.
        options.NumberHandling = JsonNumberHandling.Strict;
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
