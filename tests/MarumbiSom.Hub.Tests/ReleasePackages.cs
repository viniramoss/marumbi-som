using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Hub.Tests;

/// <summary>Pacotes do agente assinados para os testes (D27 b); também usado pelos testes do agente.</summary>
public static class ReleasePackages
{
    private static readonly string[] AgentFiles = ["MarumbiSom.Agent.exe", "MarumbiSom.Updater.exe", ReleaseSigning.PublicKeyFileName];

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

    public static ReleaseFiles SignedZip(string version, string privateKey, byte[] zip)
    {
        var sha = Sha(zip);
        var signature = ReleaseSigning.Sign(privateKey, version, sha);
        return new ReleaseFiles(zip, JsonSerializer.SerializeToUtf8Bytes(new ReleaseSignature(version, sha, signature), ContractJson.Options), signature);
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static async Task<HttpResponseMessage> UploadAsync(HttpClient admin, ReleaseFiles files)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(files.Zip), "package", "MarumbiSom.Agent-x.zip");
        if (files.Signature is not null)
        {
            form.Add(new ByteArrayContent(files.Signature), "signature", "MarumbiSom.Agent-x.sig.json");
        }

        return await admin.PostAsync("/api/releases", form);
    }
}
