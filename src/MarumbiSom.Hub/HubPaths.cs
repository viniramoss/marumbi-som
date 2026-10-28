namespace MarumbiSom.Hub;

/// <summary>Pastas do Hub (seção 6.2). Padrão no Windows: C:\MarumbiSom\Hub\data.</summary>
public sealed class HubPaths
{
    public HubPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        Data = Path.GetFullPath(dataDirectory);
    }

    public static string DefaultDataDirectory { get; } = OperatingSystem.IsWindows()
        ? @"C:\MarumbiSom\Hub\data"
        : Path.Combine(AppContext.BaseDirectory, "data");

    public string Data { get; }

    public string Database => Path.Combine(Data, "marumbi.db");

    public string Library => Path.Combine(Data, "library");

    public string Originals => Path.Combine(Data, "originals");

    /// <summary>Uploads aguardando processamento; sobrevivem a reinícios do Hub.</summary>
    public string Incoming => Path.Combine(Data, "incoming");

    /// <summary>Arquivos temporários da ingestão.</summary>
    public string Work => Path.Combine(Data, "work");

    public string Releases => Path.Combine(Data, "releases");

    public string Backups => Path.Combine(Data, "backups");

    /// <summary>Chaves que cifram o cookie do painel; sem elas guardadas, todo reinício derrubaria as sessões.</summary>
    public string Keys => Path.Combine(Data, "keys");

    /// <summary>Logs ficam ao lado de data\ (C:\MarumbiSom\Hub\logs).</summary>
    public string Logs => Path.Combine(Path.GetDirectoryName(Data) ?? Data, "logs");

    /// <summary>library\ab\abcdef….m4a (dois primeiros caracteres do hash como subpasta).</summary>
    public string LibraryFile(string hash) => Path.Combine(Library, hash[..2], hash + ".m4a");

    public void EnsureCreated()
    {
        foreach (var directory in new[] { Data, Library, Originals, Incoming, Work, Releases, Backups })
        {
            Directory.CreateDirectory(directory);
        }
    }
}
