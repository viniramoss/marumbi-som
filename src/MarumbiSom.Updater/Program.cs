using System.Globalization;
using MarumbiSom.Contracts.Releases;
using MarumbiSom.Updater;

// MarumbiSom.Updater --pending C:\ProgramData\MarumbiSom\updates\pending.json
//   Iniciado pelo agente (D27 e), que ele mesmo vai parar; o log fica em updates\updater.log.
// MarumbiSom.Updater --hub C:\MarumbiSom\Hub
//   Rodado a cada minuto pela tarefa agendada "MarumbiSom Hub - atualizador" como SYSTEM (D32 c); sem pedido do Hub,
//   sai na hora sem gravar nada. O log fica em updater\hub-updater.log.
var hubIndex = Array.FindIndex(args, a => string.Equals(a, "--hub", StringComparison.OrdinalIgnoreCase));
if (hubIndex >= 0)
{
    if (hubIndex + 1 >= args.Length)
    {
        Console.Error.WriteLine("Uso: MarumbiSom.Updater --hub C:\\MarumbiSom\\Hub (rodado pela tarefa agendada do install-hub.ps1).");
        return 2;
    }

    return await RunHubAsync(new HubUpdatePaths(args[hubIndex + 1]));
}

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

var log = Logger(paths.Log);

// Um atualizador por vez.
using var mutex = new Mutex(initiallyOwned: true, @"Global\MarumbiSom.Updater", out var owned);
if (!owned)
{
    log("Outro atualizador já está rodando; este sai sem fazer nada.");
    return 3;
}

try
{
    var runner = new UpdateRunner(paths, new WindowsServiceControl(log, WindowsServiceControl.AgentProcesses), TimeProvider.System, log);
    var result = await runner.RunAsync(CancellationToken.None);
    return result?.Outcome == UpdateStages.Done ? 0 : 1;
}
catch (Exception ex)
{
    log($"Erro inesperado: {ex}");
    return 1;
}

static async Task<int> RunHubAsync(HubUpdatePaths hub)
{
    // O caso comum (a cada minuto): nada pedido e nenhuma troca pela metade.
    if (!File.Exists(hub.Pending) && !File.Exists(hub.State))
    {
        return 0;
    }

    // A tarefa não sobrepõe execuções, mas o Updater também pode ser rodado à mão.
    using var hubMutex = new Mutex(initiallyOwned: true, @"Global\MarumbiSom.HubUpdater", out var hubOwned);
    if (!hubOwned)
    {
        return 3;
    }

    var hubLog = Logger(hub.Log);
    try
    {
        var runner = new HubUpdateRunner(hub, new WindowsServiceControl(hubLog, WindowsServiceControl.HubProcesses), TimeProvider.System, hubLog);
        var result = await runner.RunAsync(CancellationToken.None);
        return result is null || result.Outcome == UpdateStages.Done ? 0 : 1;
    }
    catch (Exception ex)
    {
        hubLog($"Erro inesperado: {ex}");
        return 1;
    }
}

static Action<string> Logger(string path)
{
    var gate = new Lock();
    return message =>
    {
        lock (gate)
        {
            // Sem crescer para sempre: acima de 1 MB o log vira .old (um só).
            var file = new FileInfo(path);
            if (file.Exists && file.Length > 1024 * 1024)
            {
                File.Move(path, path + ".old", overwrite: true);
            }

            File.AppendAllText(path, $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}");
        }
    };
}
