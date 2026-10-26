using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MarumbiSom.Updater;

/// <summary>
/// Leitura, como SYSTEM, de arquivos que a conta do Hub pode ter preparado em <c>data\</c> (D32 d). Uma junção ou um
/// link simbólico no caminho faria o SYSTEM ler (e o Hub receber) um arquivo qualquer do PC, então o caminho precisa
/// ser exatamente o esperado: nenhuma pasta abaixo de <paramref name="trustedRoot"/> pode ser link e, no Windows, o
/// arquivo aberto precisa estar no mesmo caminho (o que também pega os links do Gerenciador de Objetos).
/// </summary>
internal static partial class SafeFiles
{
    private const uint FileNameNormalized = 0;

    public static FileStream OpenRead(string path, string trustedRoot)
    {
        var full = Path.GetFullPath(path);
        EnsureNoLinks(full, trustedRoot);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        try
        {
            if (OperatingSystem.IsWindows() && FinalPath(stream.SafeFileHandle) is var final && !string.Equals(final, full, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"{full} leva a outro arquivo ({final}); o atualizador não segue links.");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static byte[] ReadAllBytes(string path, string trustedRoot, int maxBytes)
    {
        using var stream = OpenRead(path, trustedRoot);
        if (stream.Length > maxBytes)
        {
            throw new IOException($"{path} é grande demais ({stream.Length} bytes).");
        }

        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void EnsureNoLinks(string full, string trustedRoot)
    {
        var root = Path.GetFullPath(trustedRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"{full} está fora de {root}.");
        }

        for (var current = full; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
        {
            if ((File.Exists(current) || Directory.Exists(current) || new FileInfo(current).LinkTarget is not null)
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"{current} é um link ou junção; o atualizador não segue links.");
            }
        }
    }

    private static unsafe string FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint length;
        fixed (char* pointer = buffer)
        {
            length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, FileNameNormalized);
        }

        if (length == 0 || length >= buffer.Length)
        {
            throw new IOException($"Não foi possível conferir o caminho do arquivo (erro {Marshal.GetLastPInvokeError()}).");
        }

        var path = new string(buffer, 0, (int)length);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);
}
