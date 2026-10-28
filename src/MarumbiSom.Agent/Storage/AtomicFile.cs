using System.Text;

namespace MarumbiSom.Agent.Storage;

/// <summary>Regra 12: toda escrita de arquivo de estado é atômica (grava em .tmp e substitui).</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
