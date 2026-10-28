using System.Net.WebSockets;

namespace MarumbiSom.Agent.Sync;

/// <summary>Como o agente fala com o Hub (HTTP e WebSocket). Os testes trocam pela conexão em memória do TestServer.</summary>
public interface IHubTransport
{
    HttpClient CreateClient(Uri baseAddress, TimeSpan timeout);

    Task<WebSocket> ConnectAsync(Uri uri, string token, CancellationToken cancellationToken);
}

/// <summary>HttpClient do <see cref="IHttpClientFactory"/> (seção 13.2) e <see cref="ClientWebSocket"/>.</summary>
public sealed class HttpHubTransport(IHttpClientFactory factory) : IHubTransport
{
    public const string ClientName = "hub";

    public HttpClient CreateClient(Uri baseAddress, TimeSpan timeout)
    {
        var client = factory.CreateClient(ClientName);
        client.BaseAddress = baseAddress;
        client.Timeout = timeout;
        return client;
    }

    public async Task<WebSocket> ConnectAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        try
        {
            await socket.ConnectAsync(uri, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
