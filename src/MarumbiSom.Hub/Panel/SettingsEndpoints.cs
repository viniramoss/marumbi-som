using MarumbiSom.Hub.Updates;

namespace MarumbiSom.Hub.Panel;

/// <summary>Tela de Configurações (seção 7.2): versão do Hub, espaço em disco e situação do backup diário.</summary>
public static class SettingsEndpoints
{
    public static void MapSettings(this RouteGroupBuilder api) =>
        api.MapGet("/settings", (HubPaths paths) => Results.Ok(Read(paths))).Returns<SettingsInfo>();

    public static SettingsInfo Read(HubPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var backups = Directory.Exists(paths.Backups)
            ? new DirectoryInfo(paths.Backups).GetFiles("marumbi-*.db").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList()
            : [];
        var last = backups.FirstOrDefault();
        return new SettingsInfo(
            HubUpdates.CurrentVersion,
            paths.Data,
            DiskFreeMb(paths.Data),
            new BackupInfo(
                last is null ? null : new DateTimeOffset(last.LastWriteTimeUtc, TimeSpan.Zero),
                last?.Name,
                last?.Length,
                backups.Count,
                backups.Sum(b => b.Length),
                paths.Backups));
    }


    private static long? DiskFreeMb(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is null ? null : new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public sealed record BackupInfo(DateTimeOffset? LastAt, string? LastFile, long? LastSizeBytes, int Count, long TotalBytes, string Directory);

    public sealed record SettingsInfo(string HubVersion, string DataDirectory, long? DiskFreeMb, BackupInfo Backup);
}
