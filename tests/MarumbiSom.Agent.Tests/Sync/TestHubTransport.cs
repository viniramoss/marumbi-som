using System.Collections.Concurrent;
using System.Net.WebSockets;
using MarumbiSom.Agent.Sync;
using Microsoft.AspNetCore.TestHost;

namespace MarumbiSom.Agent.Tests.Sync;

/// <summary>Conexão do agente com o Hub em memória, com ganchos para simular queda de rede e dados corrompidos.</summary>
internal sealed class TestHubTransport(TestServer server) : IHubTransport
{
    /// <summary>Pedidos feitos ao Hub: caminho e cabeçalho Range.</summary>
    public ConcurrentQueue<(string Path, string? Range)> Requests { get; } = new();

    /// <summary>Hub fora do ar: todo pedido e conexão falham.</summary>
    public bool Offline { get; set; }

    /// <summary>Troca a resposta de um pedido (ex.: cortar o download no meio).</summary>
    public Func<HttpRequestMessage, HttpResponseMessage, Task<HttpResponseMessage>>? Intercept { get; set; }

    public int CountFileRequests(string hash) => Requests.Count(r => r.Path == $"/agent/files/{hash}");

    public static bool IsFile(HttpRequestMessage request, string hash) => request.RequestUri!.AbsolutePath == $"/agent/files/{hash}";

    public HttpClient CreateClient(Uri baseAddress, TimeSpan timeout) =>
        new(new Interceptor(this, server.CreateHandler())) { BaseAddress = baseAddress, Timeout = timeout };

    public async Task<WebSocket> ConnectAsync(Uri uri, string token, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("Hub fora do ar (teste)");
        }

        var client = server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + token;
        return await client.ConnectAsync(uri, cancellationToken);
    }

    /// <summary>Resposta com o corpo cortado depois de <paramref name="bytes"/> bytes, como numa queda de conexão.</summary>
    public static async Task<HttpResponseMessage> CutAfterAsync(HttpResponseMessage response, int bytes)
    {
        var body = await response.Content.ReadAsByteArrayAsync();
        var range = response.Content.Headers.ContentRange;
        response.Content = new StreamContent(new CutStream(body, bytes));
        response.Content.Headers.ContentRange = range;
        return response;
    }

    /// <summary>Resposta com um byte trocado no meio.</summary>
    public static async Task<HttpResponseMessage> CorruptAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsByteArrayAsync();
        body[body.Length / 2] ^= 0xFF;
        response.Content = new ByteArrayContent(body);
        return response;
    }

    private sealed class Interceptor(TestHubTransport owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            owner.Requests.Enqueue((request.RequestUri!.AbsolutePath, request.Headers.Range?.ToString()));
            if (owner.Offline)
            {
                throw new HttpRequestException("Hub fora do ar (teste)");
            }

            var response = await base.SendAsync(request, cancellationToken);
            return owner.Intercept is { } intercept ? await intercept(request, response) : response;
        }
    }

    private sealed class CutStream(byte[] data, int cutAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var limit = Math.Min(cutAfter, data.Length);
            if (_position >= limit)
            {
                throw new IOException("Conexão caiu (teste).");
            }

            var read = Math.Min(count, limit - _position);
            Array.Copy(data, _position, buffer, offset, read);
            _position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
