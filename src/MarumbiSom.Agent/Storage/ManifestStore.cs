using System.Text.Json;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Manifests;

namespace MarumbiSom.Agent.Storage;

/// <summary>
/// Guarda <c>active.json</c> e <c>pending.json</c> (seção 8.2). Rejeita versões menores que a ativa e
/// detecta quando <c>active.json</c> foi trocado por fora (ex.: <c>--build-manifest</c>).
/// </summary>
public sealed class ManifestStore(AgentDataPaths paths, ILogger<ManifestStore> logger)
{
    private static readonly JsonSerializerOptions IndentedOptions = new(ContractJson.Options) { WriteIndented = true };

    private readonly Lock _lock = new();
    private DateTime _activeWriteTime;

    public event EventHandler<Manifest>? ActiveChanged;

    public Manifest? Active { get; private set; }

    public Manifest? Pending { get; private set; }

    /// <summary>Carrega os manifestos do disco. Arquivo corrompido é registrado e ignorado.</summary>
    public void Load()
    {
        lock (_lock)
        {
            Active = Read(paths.ActiveManifest);
            Pending = Read(paths.PendingManifest);
            _activeWriteTime = File.Exists(paths.ActiveManifest) ? File.GetLastWriteTimeUtc(paths.ActiveManifest) : default;
        }

        if (Active is not null)
        {
            logger.LogInformation("Manifesto ativo: loja {Store}, versão {Version}", Active.StoreId, Active.Version);
        }
        else
        {
            logger.LogWarning("Nenhum manifesto ativo em {Path}", paths.ActiveManifest);
        }
    }

    /// <summary>Recarrega <c>active.json</c> se ele mudou no disco desde a última leitura.</summary>
    public bool ReloadIfChangedOnDisk()
    {
        if (!File.Exists(paths.ActiveManifest))
        {
            return false;
        }

        var writeTime = File.GetLastWriteTimeUtc(paths.ActiveManifest);
        if (writeTime == _activeWriteTime)
        {
            return false;
        }

        var manifest = Read(paths.ActiveManifest);
        lock (_lock)
        {
            _activeWriteTime = writeTime;
            if (manifest is null || (Active is not null && manifest.Version < Active.Version))
            {
                return false;
            }

            Active = manifest;
        }

        logger.LogInformation("Manifesto ativo recarregado do disco: versão {Version}", manifest.Version);
        ActiveChanged?.Invoke(this, manifest);
        return true;
    }

    /// <summary>
    /// Grava um manifesto como pendente. Versões menores ou iguais à ativa da mesma loja são rejeitadas; a versão é
    /// contada por loja, então o manifesto do Hub substitui um local (<c>--build-manifest</c>) mesmo com número menor.
    /// </summary>
    public bool SavePending(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_lock)
        {
            if (Active is not null && Active.StoreId == manifest.StoreId && manifest.Version <= Active.Version)
            {
                logger.LogInformation("Manifesto versão {Version} ignorado: ativo já é {Active}", manifest.Version, Active.Version);
                return false;
            }

            AtomicFile.WriteAllText(paths.PendingManifest, Serialize(manifest));
            Pending = manifest;
            return true;
        }
    }

    /// <summary>Promove o pendente a ativo (regra 2: só chamar com todos os arquivos verificados no cache).</summary>
    public Manifest PromotePending()
    {
        Manifest promoted;
        lock (_lock)
        {
            promoted = Pending ?? throw new InvalidOperationException("Não há manifesto pendente.");
            WriteActive(promoted);
            File.Delete(paths.PendingManifest);
            Pending = null;
        }

        logger.LogInformation("Manifesto versão {Version} aplicado", promoted.Version);
        ActiveChanged?.Invoke(this, promoted);
        return promoted;
    }

    /// <summary>Grava diretamente o ativo. Usado pela ferramenta de manifesto local da Etapa 2.</summary>
    public void SaveActive(Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_lock)
        {
            if (Active is not null && manifest.Version < Active.Version)
            {
                throw new InvalidOperationException($"Versão {manifest.Version} menor que a ativa ({Active.Version}).");
            }

            WriteActive(manifest);
        }

        ActiveChanged?.Invoke(this, manifest);
    }

    public static string Serialize(Manifest manifest) => JsonSerializer.Serialize(manifest, IndentedOptions);

    private void WriteActive(Manifest manifest)
    {
        AtomicFile.WriteAllText(paths.ActiveManifest, Serialize(manifest));
        Active = manifest;
        _activeWriteTime = File.GetLastWriteTimeUtc(paths.ActiveManifest);
    }

    private Manifest? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), ContractJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            logger.LogError(ex, "Manifesto inválido em {Path}; ignorado", path);
            return null;
        }
    }
}
