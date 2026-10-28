using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace MarumbiSom.Updater;

/// <summary>O serviço MarumbiSom.Agent pelo SCM do Windows.</summary>
public sealed class WindowsServiceControl(Action<string> log) : IServiceControl
{
    private static readonly string[] ProgramProcessNames = ["MarumbiSom.Agent", "mpv"];
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(15);

    public async Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var service = new ServiceController(serviceName);
        if (service.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (service.Status != ServiceControllerStatus.StopPending)
        {
            service.Stop();
        }

        await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, timeout), cancellationToken);
    }

    public async Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var service = new ServiceController(serviceName);
        if (service.Status is ServiceControllerStatus.StopPending)
        {
            await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, timeout), cancellationToken);
        }

        if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            return;
        }

        service.Start();
        await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, timeout), cancellationToken);
    }

    public void KillLeftovers(string programDirectory)
    {
        // O Windows marca o serviço como parado antes de o processo terminar de sair: dá um tempo para ele (e os mpv
        // que ele fecha) saírem sozinhos antes de encerrar à força.
        var leftovers = ProgramProcesses(programDirectory);
        var deadline = DateTime.UtcNow + ExitGrace;
        foreach (var process in leftovers)
        {
            var remaining = deadline - DateTime.UtcNow;
            try
            {
                process.WaitForExit(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                log($"Não foi possível esperar o processo {process.Id}: {ex.Message}");
            }
        }

        foreach (var process in leftovers)
        {
            using (process)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    log($"Encerrando {process.ProcessName} (PID {process.Id}) que ainda rodava da pasta do programa");
                    // Só o processo: o próprio Updater é filho do agente e morreria junto com a árvore. Os mpv entram
                    // na lista pelo nome.
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    log($"Não foi possível encerrar o processo {process.Id}: {ex.Message}");
                }
            }
        }
    }

    private List<Process> ProgramProcesses(string programDirectory)
    {
        var root = Path.GetFullPath(programDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var found = new List<Process>();
        foreach (var process in ProgramProcessNames.SelectMany(Process.GetProcessesByName))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                log($"Não foi possível conferir o processo {process.Id}: {ex.Message}");
            }

            process.Dispose();
        }

        return found;
    }
}
