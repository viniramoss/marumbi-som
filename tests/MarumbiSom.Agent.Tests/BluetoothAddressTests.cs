using MarumbiSom.Agent.Windows.Abstractions;

namespace MarumbiSom.Agent.Tests;

public class BluetoothAddressTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", 0xAABBCCDDEEFFUL)]
    [InlineData("aa-bb-cc-dd-ee-ff", 0xAABBCCDDEEFFUL)]
    [InlineData("001122334455", 0x001122334455UL)]
    public void Formatos_aceitos(string text, ulong expected)
    {
        Assert.Equal(expected, BluetoothAddress.Parse(text).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("GG:BB:CC:DD:EE:FF")]
    [InlineData(null)]
    public void Formatos_invalidos(string? text)
    {
        Assert.False(BluetoothAddress.TryParse(text, out _));
    }

    [Fact]
    public void ToString_usa_dois_pontos_e_maiusculas()
    {
        Assert.Equal("00:1A:7D:DA:71:13", new BluetoothAddress(0x001A7DDA7113).ToString());
    }
}
