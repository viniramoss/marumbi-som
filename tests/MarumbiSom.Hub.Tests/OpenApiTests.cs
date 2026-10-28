using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MarumbiSom.Hub.Tests;

/// <summary>
/// O painel gera os tipos a partir de web/panel/openapi.json (D23 g). Este teste falha se o arquivo não for o que o Hub
/// gera agora. Para atualizar: <c>UPDATE_OPENAPI=1 dotnet test --filter OpenApi</c> e depois <c>npm run api</c> no painel.
/// </summary>
public sealed class OpenApiTests
{
    [Fact]
    public async Task Openapi_do_painel_esta_atualizado()
    {
        await using var hub = await HubHarness.StartAsync();
        var anonymous = await hub.AnonymousClient().GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var admin = await hub.AdminClientAsync();
        var generated = Normalize(await admin.GetStringAsync("/openapi/v1.json"));
        var file = Path.Combine(RepositoryRoot(), "web", "panel", "openapi.json");
        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(file, generated);
        }

        Assert.True(File.Exists(file), $"Falta {file}. Rode com UPDATE_OPENAPI=1.");
        Assert.True(
            Normalize(await File.ReadAllTextAsync(file)) == generated,
            "web/panel/openapi.json está desatualizado. Rode UPDATE_OPENAPI=1 dotnet test --filter OpenApi e npm run api em web/panel.");
    }

    private static string Normalize(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MarumbiSom.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
