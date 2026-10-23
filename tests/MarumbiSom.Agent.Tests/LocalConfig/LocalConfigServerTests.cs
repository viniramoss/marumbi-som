using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MarumbiSom.Agent.LocalConfig;
using MarumbiSom.Agent.Simulation;
using MarumbiSom.Agent.Tests.Playback;
using MarumbiSom.Agent.Windows.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarumbiSom.Agent.Tests.LocalConfig;

public sealed class LocalConfigServerTests : IAsyncLifetime, IDisposable
{
    private const string Password = "senha-forte-123";
    private static readonly BluetoothAddress Paired = BluetoothAddress.Parse("AA:BB:CC:DD:EE:FF");

    private readonly PlayerHarness _player = new();
    private readonly SimulatedWindowsEnvironment _windows = new(Paired);
    private readonly FakeSpeakerSelection _selection = new();
    private LocalConfigFile _config = null!;
    private LocalAuth _auth = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _config = new LocalConfigFile(_player.Paths);
        _auth = new LocalAuth(_config, _player.Time, NullLogger<LocalAuth>.Instance);
        var context = new LocalConfigContext(
            _auth, _config, _player.Engine, _player.Bluetooth, _selection, _windows, _player.Manifests, _player.Paths.LogsDirectory, _player.Time);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        _app = builder.Build();
        _app.MapLocalConfig(context);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    public void Dispose() => _player.Dispose();

    [Fact]
    public async Task Pagina_tem_cabecalhos_de_seguranca()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Configurar Som", await response.Content.ReadAsStringAsync());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/app.js")).StatusCode);
    }

    [Fact]
    public async Task Sem_senha_definida_nao_entra()
    {
        var response = await PostAsync("/api/login", new { password = "qualquer" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("--set-local-password", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cinco_senhas_erradas_bloqueiam_por_15_minutos()
    {
        _auth.SetPassword(Password);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/api/login", new { password = "errada" })).StatusCode);
        }

        Assert.Equal((HttpStatusCode)423, (await PostAsync("/api/login", new { password = "errada" })).StatusCode);
        Assert.Equal((HttpStatusCode)423, (await PostAsync("/api/login", new { password = Password })).StatusCode);

        _player.Time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/login", new { password = Password })).StatusCode);
    }

    [Fact]
    public async Task Rotas_protegidas_exigem_sessao()
    {
        _auth.SetPassword(Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/status")).StatusCode);

        await LoginAsync();
        var status = await GetJsonAsync("/api/status");

        Assert.Equal("stopped", status.GetProperty("player").GetProperty("status").GetString());
        Assert.Equal("connected", status.GetProperty("bluetooth").GetString());
    }

    [Fact]
    public async Task Post_sem_cabecalho_ou_de_outra_origem_e_recusado()
    {
        _auth.SetPassword(Password);

        var noHeader = await _client.PostAsJsonAsync("/api/login", new { password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, noHeader.StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/login") { Content = JsonContent.Create(new { password = Password }) };
        foreign.Headers.Add(LocalConfigEndpoints.CsrfHeader, "1");
        foreign.Headers.Add("Origin", "https://site-malicioso.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public async Task Host_diferente_de_localhost_e_recusado()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "rebind.example:47800";

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Escolher_caixa_pareada_grava_a_configuracao_e_troca_no_guardiao()
    {
        _auth.SetPassword(Password);
        await LoginAsync();

        var devices = await GetJsonAsync("/api/devices");
        Assert.Equal("AA:BB:CC:DD:EE:FF", devices[0].GetProperty("address").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/speaker", new { address = "11:22:33:44:55:66" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/speaker", new { address = "aa:bb:cc:dd:ee:ff" })).StatusCode);

        Assert.Equal(Paired, _selection.SpeakerAddress);
        Assert.Equal("AA:BB:CC:DD:EE:FF", _config.Get("Bluetooth", "SpeakerAddress"));
    }

    [Fact]
    public async Task Trocar_senha_exige_a_atual_e_tamanho_minimo()
    {
        _auth.SetPassword(Password);
        await LoginAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/password", new { current = "errada", @new = "outra-senha-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/password", new { current = Password, @new = "curta" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/api/password", new { current = Password, @new = "outra-senha-1" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(LoginOutcome.Success, _auth.Login("outra-senha-1").Outcome);
        Assert.StartsWith("pbkdf2-sha256$", _config.Get(LocalAuth.Section, LocalAuth.PasswordKey));
    }

    [Fact]
    public async Task Logs_mostram_as_ultimas_linhas()
    {
        _auth.SetPassword(Password);
        await LoginAsync();
        Directory.CreateDirectory(_player.Paths.LogsDirectory);
        await File.WriteAllLinesAsync(Path.Combine(_player.Paths.LogsDirectory, "agent-20261023.log"), Enumerable.Range(1, 500).Select(i => $"linha {i}"));

        var text = await _client.GetStringAsync("/api/logs?lines=10");

        Assert.StartsWith("linha 491", text.TrimStart());
        Assert.DoesNotContain("linha 490\n", text);
    }

    [Fact]
    public async Task Som_de_teste_vai_para_o_player()
    {
        _auth.SetPassword(Password);
        await LoginAsync();
        using var cts = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await _player.Engine.StepAsync(CancellationToken.None);
                await Task.Delay(5, CancellationToken.None);
            }
        });

        // Sob carga (suíte inteira), o mpv de ofertas pode ainda não estar de pé quando o pedido chega.
        for (var i = 0; i < 200 && !_player.Spot.IsRunning; i++)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        var response = await PostAsync("/api/test-sound", new { });
        await cts.CancelAsync();
        await loop;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("som-de-teste.wav", _player.Spot.CurrentFile);
    }

    [Fact]
    public void Hash_da_senha_e_verificado_e_nao_guarda_a_senha()
    {
        var hash = LocalPassword.Hash(Password);

        Assert.DoesNotContain(Password, hash);
        Assert.True(LocalPassword.Verify(Password, hash));
        Assert.False(LocalPassword.Verify("outra", hash));
        Assert.False(LocalPassword.Verify(Password, "lixo"));
        Assert.NotEqual(hash, LocalPassword.Hash(Password));
    }

    private async Task LoginAsync()
    {
        var response = await PostAsync("/api/login", new { password = Password });
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        _client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(LocalConfigEndpoints.CsrfHeader, "1");
        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> GetJsonAsync(string path) =>
        JsonDocument.Parse(await _client.GetStringAsync(path)).RootElement.Clone();

    private sealed class FakeSpeakerSelection : ISpeakerSelection
    {
        public BluetoothAddress? SpeakerAddress { get; private set; }

        public void SetSpeaker(BluetoothAddress? address) => SpeakerAddress = address;
    }
}
