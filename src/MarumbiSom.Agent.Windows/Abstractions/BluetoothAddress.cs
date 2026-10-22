using System.Globalization;

namespace MarumbiSom.Agent.Windows.Abstractions;

/// <summary>Endereço Bluetooth (MAC) de 48 bits. A caixa é identificada por ele, nunca pelo nome (seção 9.4).</summary>
public readonly record struct BluetoothAddress(ulong Value)
{
    public static BluetoothAddress Parse(string text)
    {
        if (!TryParse(text, out var address))
        {
            throw new FormatException($"Endereço Bluetooth inválido: '{text}'. Formato esperado: AA:BB:CC:DD:EE:FF.");
        }

        return address;
    }

    public static bool TryParse(string? text, out BluetoothAddress address)
    {
        address = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hex = text.Trim().Replace(":", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (hex.Length != 12 || !ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        address = new BluetoothAddress(value);
        return true;
    }

    public override string ToString()
    {
        var hex = Value.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
