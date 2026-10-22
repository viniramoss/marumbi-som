using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarumbiSom.Contracts.Json;

/// <summary>Serializa <see cref="TimeOnly"/> no formato "HH:mm" usado em agendas e ofertas de horário fixo.</summary>
public sealed class HhMmTimeOnlyConverter : JsonConverter<TimeOnly>
{
    private const string Format = "HH:mm";

    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (text is not null && TimeOnly.TryParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        // "24:00" representa o fim do dia em agendas que vão até a meia-noite.
        if (text == "24:00")
        {
            return TimeOnly.MaxValue;
        }

        throw new JsonException($"Horário inválido '{text}'. Formato esperado: HH:mm.");
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value == TimeOnly.MaxValue ? "24:00" : value.ToString(Format, CultureInfo.InvariantCulture));
    }
}
