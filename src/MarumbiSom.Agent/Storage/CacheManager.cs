using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Agent.Storage;

/// <summary>
/// Cache local de áudios, um arquivo por SHA-256 (regra 7) em <c>cache\ab\abcdef….m4a</c>.
/// Nunca apaga arquivo referenciado pelo manifesto ativo ou pendente (regra 4).
/// </summary>
public sealed partial class CacheManager(AgentDataPaths paths, TimeProvider time, ILogger<CacheManager> logger)
{
    public static readonly TimeSpan UnreferencedRetention = TimeSpan.FromDays(7);
    public const long DefaultMaxBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>Arquivo corrompido apagado; o SyncEngine baixa de novo.</summary>
    public event EventHandler<string>? FileRemoved;

    public static bool IsValidHash(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    public string PathFor(string hash)
    {
        if (!IsValidHash(hash))
        {
            throw new ArgumentException($"Hash inválido: '{hash}'.", nameof(hash));
        }

        return Path.Combine(paths.CacheDirectory, hash[..2], hash + ".m4a");
    }

    public bool Has(string hash) => IsValidHash(hash) && File.Exists(PathFor(hash));

    /// <summary>Hashes referenciados pelo manifesto que não estão no cache.</summary>
    public IReadOnlyList<string> Missing(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Tracks.Keys.Where(h => !Has(h)).ToList();
    }

    /// <summary>Confere o SHA-256 de um arquivo do cache. Arquivo corrompido é apagado.</summary>
    public async Task<bool> VerifyAsync(string hash, CancellationToken cancellationToken)
    {
        var path = PathFor(hash);
        if (!File.Exists(path))
        {
            return false;
        }

        var actual = await ComputeHashAsync(path, cancellationToken);
        if (actual == hash)
        {
            return true;
        }

        logger.LogWarning("Arquivo do cache corrompido ({Hash}, calculado {Actual}); apagado", hash, actual);
        File.Delete(path);
        FileRemoved?.Invoke(this, hash);
        return false;
    }

    /// <summary>Adiciona um arquivo ao cache pelo seu conteúdo e devolve o hash.</summary>
    public async Task<string> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var hash = await ComputeHashAsync(sourcePath, cancellationToken);
        var destination = PathFor(hash);
        if (!File.Exists(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = Path.Combine(paths.PartialDirectory, hash + ".part");
            Directory.CreateDirectory(paths.PartialDirectory);
            File.Copy(sourcePath, temp, overwrite: true);
            File.Move(temp, destination, overwrite: true);
        }

        return hash;
    }

    public static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Limpeza (seção 8.2): remove arquivos não referenciados há mais de 7 dias e, acima do limite total,
    /// os não referenciados mais antigos. <paramref name="unreferencedSince"/> é atualizado e deve ser persistido.
    /// </summary>
    public int Cleanup(IReadOnlySet<string> referenced, IDictionary<string, DateTimeOffset> unreferencedSince, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(referenced);
        ArgumentNullException.ThrowIfNull(unreferencedSince);
        if (!Directory.Exists(paths.CacheDirectory))
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var files = Directory.EnumerateFiles(paths.CacheDirectory, "*.m4a", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Where(f => IsValidHash(Path.GetFileNameWithoutExtension(f.Name)))
            .ToList();

        foreach (var hash in unreferencedSince.Keys.ToList())
        {
            if (referenced.Contains(hash) || !files.Any(f => Path.GetFileNameWithoutExtension(f.Name) == hash))
            {
                unreferencedSince.Remove(hash);
            }
        }

        var removed = 0;
        var candidates = new List<(FileInfo File, DateTimeOffset Since)>();
        foreach (var file in files)
        {
            var hash = Path.GetFileNameWithoutExtension(file.Name);
            if (referenced.Contains(hash))
            {
                continue;
            }

            if (!unreferencedSince.TryGetValue(hash, out var since))
            {
                unreferencedSince[hash] = since = now;
            }

            if (now - since >= UnreferencedRetention)
            {
                removed += Delete(file, hash, unreferencedSince, "não referenciado há mais de 7 dias");
            }
            else
            {
                candidates.Add((file, since));
            }
        }

        var total = files.Where(f => f.Exists).Sum(f => f.Length);
        foreach (var (file, _) in candidates.OrderBy(c => c.Since))
        {
            if (total <= maxBytes)
            {
                break;
            }

            total -= file.Length;
            removed += Delete(file, Path.GetFileNameWithoutExtension(file.Name), unreferencedSince, "limite do cache");
        }

        return removed;
    }

    private int Delete(FileInfo file, string hash, IDictionary<string, DateTimeOffset> unreferencedSince, string reason)
    {
        try
        {
            file.Delete();
            file.Refresh();
            unreferencedSince.Remove(hash);
            logger.LogInformation("Removido do cache ({Reason}): {Hash}", reason, hash);
            return 1;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Falha ao remover {Hash} do cache", hash);
            return 0;
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();
}
