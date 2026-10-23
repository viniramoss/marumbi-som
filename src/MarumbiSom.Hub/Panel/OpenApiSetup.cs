using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MarumbiSom.Hub.Panel;

/// <summary>
/// OpenAPI da API do painel (D23 g). O arquivo gerado vai para web/panel/openapi.json, de onde saem os tipos do painel.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentPath = "/openapi/v1.json";

    /// <summary>Corpos de requisição: só os campos não nulos são obrigatórios (ex.: PATCH com só o nome).</summary>
    private static readonly string[] InputSuffixes = ["Input", "Update", "Request", "Change"];

    public static IServiceCollection AddPanelOpenApi(this IServiceCollection services) => services.AddOpenApi(options =>
    {
        options.ShouldInclude = description => description.RelativePath?.StartsWith("api/", StringComparison.Ordinal) == true;
        options.AddSchemaTransformer((schema, context, cancellationToken) =>
        {
            var type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
            if (type == typeof(TimeOnly))
            {
                // Horários vão como "HH:mm" (HhMmTimeOnlyConverter); "24:00" é o fim do dia.
                schema.Type = JsonSchemaType.String;
                schema.Format = null;
                schema.Pattern = "^([01][0-9]|2[0-3]):[0-5][0-9]$|^24:00$";
                schema.Examples = [JsonValue.Create("07:30")];
            }
            else if (schema.Properties is { Count: > 0 } properties && context.JsonPropertyInfo is null)
            {
                // Respostas trazem todos os campos (nulos como null, ver HubJson); entradas exigem só os não nulos.
                var input = InputSuffixes.Any(suffix => type.Name.EndsWith(suffix, StringComparison.Ordinal));
                schema.Required = new HashSet<string>(
                    properties.Where(p => !input || p.Value.Type is not { } t || !t.HasFlag(JsonSchemaType.Null)).Select(p => p.Key),
                    StringComparer.Ordinal);
            }

            return Task.CompletedTask;
        });
        options.AddDocumentTransformer(async (document, context, cancellationToken) =>
        {
            document.Info.Title = "Marumbi Som — Hub";
            document.Info.Description = "API do painel (seção 11.2). Gerado pelo Hub; não editar à mão.";
            document.Servers?.Clear();
            // Mensagens do WebSocket /ws/panel não passam por nenhuma rota; entram aqui para o painel ter os tipos.
            var message = await context.GetOrCreateSchemaAsync(typeof(PanelMessage), cancellationToken: cancellationToken);
            // O mapeamento do discriminador aponta para esquemas que só existiriam se alguma rota usasse os subtipos; o
            // campo "type" de cada opção do anyOf já distingue as mensagens.
            message.Discriminator = null;
            message.Required?.Clear();
            message.Properties?.Clear();
            document.Components ??= new OpenApiComponents();
            document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
            document.Components.Schemas["PanelMessage"] = message;
        });
    });

    public static void MapPanelOpenApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapOpenApi(DocumentPath).RequireAuthorization(PanelAuth.Policy);
    }
}
