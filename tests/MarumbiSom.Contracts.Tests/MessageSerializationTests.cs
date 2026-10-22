using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Manifests;
using MarumbiSom.Contracts.Messages;

namespace MarumbiSom.Contracts.Tests;

public class MessageSerializationTests
{
    [Fact]
    public void Status_do_exemplo_da_secao_11_4_e_desserializado()
    {
        const string json = """
            { "type": "status", "player": "playing", "nowPlaying": "a3f9", "positionMs": 52000,
              "volume": 70, "bluetooth": "connected", "pendingVersion": 48, "syncProgress": { "done": 3, "total": 10 },
              "diskFreeMb": 81234, "uptimeS": 7200 }
            """;

        var status = Assert.IsType<StatusMessage>(JsonSerializer.Deserialize<AgentMessage>(json, ContractJson.Options));

        Assert.Equal(PlayerStatus.Playing, status.Player);
        Assert.Equal(BluetoothStatus.Connected, status.Bluetooth);
        Assert.Equal(new SyncProgress(3, 10), status.SyncProgress);
        Assert.Equal(48, status.PendingVersion);
    }

    [Fact]
    public void Mensagens_do_agente_incluem_o_campo_type()
    {
        AgentMessage message = new SpotSkippedMessage("of-12", "pl-3", SpotSkipReasons.Stale);

        var json = JsonSerializer.Serialize(message, ContractJson.Options);

        Assert.Equal("""{"type":"spot.skipped","offerId":"of-12","placementId":"pl-3","reason":"stale"}""", json);
    }

    [Fact]
    public void Spot_started_serializa_modo_e_data_com_fuso()
    {
        AgentMessage message = new SpotStartedMessage("of-12", "pl-2", SpotMode.During, new DateTimeOffset(2026, 10, 22, 16, 22, 25, TimeSpan.FromHours(-3)));

        var json = JsonSerializer.Serialize(message, ContractJson.Options);

        Assert.Contains("\"mode\":\"during\"", json, StringComparison.Ordinal);
        Assert.Contains("\"at\":\"2026-10-22T16:22:25-03:00\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Comando_do_hub_e_desserializado_com_argumentos()
    {
        const string json = """{ "type": "command", "id": "c-123", "command": "setVolume", "args": { "volume": 60 } }""";

        var command = Assert.IsType<CommandMessage>(JsonSerializer.Deserialize<HubMessage>(json, ContractJson.Options));

        Assert.Equal("c-123", command.Id);
        Assert.Equal(CommandTypes.SetVolume, command.Command);
        Assert.Equal(60, command.Args!["volume"]!.GetValue<int>());
        Assert.Contains(command.Command, CommandTypes.All);
    }

    [Fact]
    public void Manifest_changed_e_desserializado()
    {
        var message = JsonSerializer.Deserialize<HubMessage>("""{ "type": "manifest.changed", "version": 48 }""", ContractJson.Options);

        Assert.Equal(new ManifestChangedMessage(48), message);
    }

    [Fact]
    public void Tipo_desconhecido_e_rejeitado()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<HubMessage>("""{ "type": "formatar.disco" }""", ContractJson.Options));
    }
}
