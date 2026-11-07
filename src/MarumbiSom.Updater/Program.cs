using System.Globalization;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Updater;

// MarumbiSom.Updater --pending C:\ProgramData\MarumbiSom\updates\pending.json
// Iniciado pelo agente (D27 e), que ele mesmo vai parar; o log fica em updates\updater.log.
var index = Array.FindIndex(args, a => string.Equals(a, "--pending", StringComparison.OrdinalIgnoreCase));
if (index < 0 || index + 1 >= args.Length)
{
    Console.Error.WriteLine("Uso: MarumbiSom.Updater --pending <data>\\updates\\pending.json (iniciado pelo agente).");
    return 2;
}

var pendingFile = Path.GetFullPath(args[index + 1]);
var updatesDirectory = Path.GetDirectoryName(pendingFile)!;
var paths = new UpdatePaths(Path.GetDirectoryName(updatesDirectory)!);
if (!string.Equals(Path.GetFullPath(paths.Pending), pendingFile, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"O pedido precisa estar em ...\\updates\\pending.json, não em {pendingFile}.");
    return 2;
}

var logLock = new Lock();
void Log(string message)
{
    lock (logLock)
    {
        File.AppendAllText(paths.Log, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}");
    }
}

// Um atualizador por vez.
using var mutex = new Mutex(initiallyOwned: true, @"Global\MarumbiSom.Updater", out var owned);
if (!owned)
{
    Log("Outro atualizador já está rodando; este sai sem fazer nada.");
    return 3;
}

try
{
    var runner = new UpdateRunner(paths, new WindowsServiceControl(Log), TimeProvider.System, Log);
    var result = await runner.RunAsync(CancellationToken.None);
    return result?.Outcome == UpdateStages.Done ? 0 : 1;
}
catch (Exception ex)
{
    Log($"Erro inesperado: {ex}");
    return 1;
}
