using System.Net;
using System.Net.Http.Json;
using MarumbiSom.Hub.Panel;

namespace MarumbiSom.Hub.Tests;

public sealed class PanelAuthTests
{
    [Fact]
    public async Task Api_exige_login()
    {
        await using var hub = await HubHarness.StartAsync();

        var response = await hub.AnonymousClient().GetAsync("/api/stores");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_valido_abre_sessao_com_cookie_seguro()
    {
        await using var hub = await HubHarness.StartAsync();
        var client = hub.AnonymousClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = HubHarness.AdminUser, password = HubHarness.AdminPassword });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(PanelAuth.CookieName, StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        // Seção 7.2: sessão de 30 dias por dispositivo (cookie persistente, sobrevive a fechar o navegador).
        var expires = DateTimeOffset.Parse(
            cookie.Split(';').Select(p => p.Trim()).Single(p => p.StartsWith("expires=", StringComparison.Ordinal))["expires=".Length..],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange((expires - hub.Time.GetUtcNow()).TotalDays, 29.5, 31);
        var admin = await hub.AdminClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/stores")).StatusCode);
        Assert.Equal(HubHarness.AdminUser, (await HubHarness.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("username").GetString());
    }

    [Fact]
    public async Task Senha_errada_e_limitada_a_cinco_tentativas_por_minuto()
    {
        await using var hub = await HubHarness.StartAsync();
        var client = hub.AnonymousClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new { username = HubHarness.AdminUser, password = "errada-" + i })).StatusCode);
        }

        Assert.All(statuses.Take(5), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[5]);
        var audit = await hub.WithDbAsync(db => Task.FromResult(db.AuditLog.Count(a => a.Action == "login.failed")));
        Assert.Equal(5, audit);
    }

    [Fact]
    public async Task Escrita_sem_cabecalho_anti_csrf_e_recusada()
    {
        await using var hub = await HubHarness.StartAsync();
        var admin = await hub.AdminClientAsync();
        admin.DefaultRequestHeaders.Remove(PanelAuth.CsrfHeader);

        var response = await admin.PostAsJsonAsync("/api/playlists", new { name = "Manhã" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
