using System.ServiceProcess;
using MarumbiSom.Agent.Windows.Abstractions;
using Microsoft.Extensions.Logging;

namespace MarumbiSom.Agent.Windows.Implementations;

public sealed class WindowsSystemServices(ILogger<WindowsSystemServices> logger) : ISystemServices
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public bool IsRunning(string serviceName)
    {
        using var service = new ServiceController(serviceName);
        try
        {
            return service.Status == ServiceControllerStatus.Running;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Serviço {Service} não encontrado", serviceName);
            return false;
        }
    }

    public Task StartAsync(string serviceName, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var service = new ServiceController(serviceName);
                if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                {
                    return;
                }

                logger.LogInformation("Iniciando o serviço {Service}", serviceName);
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
            },
            cancellationToken);

    public Task RestartAsync(string serviceName, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                using var service = new ServiceController(serviceName);
                logger.LogWarning("Reiniciando o serviço {Service}", serviceName);
                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop(stopDependentServices: true);
                    service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
                }

                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
            },
            cancellationToken);
}
