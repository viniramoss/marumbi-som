using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Hub.Tests;

/// <summary>Pacotes do agente e do Hub assinados para os testes (D27 b, D32 a); também usado pelos testes do agente.</summary>
public static class ReleasePackages
{
    private static readonly string[] AgentFiles = ["MarumbiSom.Agent.exe", "MarumbiSom.Updater.exe", ReleaseSigning.PublicKeyFileName];

    public static readonly string[] HubFiles =
        ["MarumbiSom.Hub.exe", "MarumbiSom.Hub.dll", "MarumbiSom.Updater.exe", ReleaseSigning.PublicKeyFileName, "wwwroot/index.html", "tools/ffmpeg.exe"];

    public sealed record ReleaseFiles(byte[] Zip, byte[]? Signature, string SignatureText);

    public static ReleaseFiles Package(string version, string privateKey, string[]? files = null, string? extra = null, string extraName = "leia-me.txt")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in files ?? AgentFiles)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write($"{name} {version}");
            }

            if (extra is not null)
            {
                using var writer = new StreamWriter(zip.CreateEntry(extraName).Open());
                writer.Write(extra);
            }
        }

        return SignedZip(version, privateKey, buffer.ToArray());
    }

    /// <summary>Pacote do Hub com a assinatura própria do Hub (ou de outro <paramref name="product"/>, para os testes de recusa).</summary>
    public static ReleaseFiles HubPackage(string version, string privateKey, string[]? files = null, string product = ReleaseProducts.Hub)
    {
        var agent = Package(version, privateKey, files ?? HubFiles);
        return SignedZip(version, privateKey, agent.Zip, product);
    }

    public static ReleaseFiles SignedZip(string version, string privateKey, byte[] zip, string product = ReleaseProducts.Agent)
    {
        var sha = Sha(zip);
        var signature = ReleaseSigning.Sign(privateKey, version, sha, product);
        return new ReleaseFiles(zip, JsonSerializer.SerializeToUtf8Bytes(new ReleaseSignature(version, sha, signature), ContractJson.Options), signature);
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static async Task<HttpResponseMessage> UploadAsync(HttpClient admin, ReleaseFiles files, string url = "/api/releases")
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(files.Zip), "package", "MarumbiSom.Agent-x.zip");
        if (files.Signature is not null)
        {
            form.Add(new ByteArrayContent(files.Signature), "signature", "MarumbiSom.Agent-x.sig.json");
        }

        return await admin.PostAsync(url, form);
    }
}
