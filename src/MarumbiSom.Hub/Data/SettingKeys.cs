using System.Globalization;

namespace MarumbiSom.Hub.Data;

/// <summary>Chaves da tabela <c>settings</c>.</summary>
public static class SettingKeys
{
    public const string FadeInS = "ingest.fadeInS";
    public const string FadeOutS = "ingest.fadeOutS";

    public static double Number(IReadOnlyDictionary<string, string> settings, string key, double fallback, double min, double max)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.TryGetValue(key, out var text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value >= min && value <= max
                ? value
                : fallback;
    }
}
