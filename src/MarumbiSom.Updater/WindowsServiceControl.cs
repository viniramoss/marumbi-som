using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace MarumbiSom.Updater;

/// <summary>O serviço MarumbiSom.Agent pelo SCM do Windows.</summary>
public sealed class WindowsServiceControl(Action<string> log) : IServiceControl
{
    private static readonly string[] ProgramProcesses = ["MarumbiSom.Agent", "mpv"];

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
        var root = Path.GetFullPath(programDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in ProgramProcesses.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    log($"Encerrando {Path.GetFileName(path)} (PID {process.Id}) que ainda rodava da pasta do programa");
                    // Só o processo: o próprio Updater é filho do agente e morreria junto com a árvore. Os mpv entram
                    // na lista pelo nome.
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    log($"Não foi possível conferir o processo {process.Id}: {ex.Message}");
                }
            }
        }
    }
}
