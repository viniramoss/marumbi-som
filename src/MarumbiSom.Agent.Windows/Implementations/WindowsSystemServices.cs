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

                // DependentServices traz os diretos e os indiretos na ordem inversa da partida; o Stop os derruba
                // junto, mas não os religa (ex.: o Audiosrv, ao reiniciar o AudioEndpointBuilder). O Stop usa essa
                // mesma lista em cache, então os itens não são descartados aqui.
                var dependents = service.DependentServices
                    .Where(d => d.Status == ServiceControllerStatus.Running)
                    .Select(d => d.ServiceName)
                    .ToList();

                if (service.Status != ServiceControllerStatus.Stopped)
                {
                    service.Stop(stopDependentServices: true);
                    service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
                }

                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
                dependents.Reverse();
                foreach (var name in dependents)
                {
                    StartDependent(name);
                }
            },
            cancellationToken);

    private void StartDependent(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
            {
                return;
            }

            logger.LogInformation("Iniciando de novo o serviço dependente {Service}", serviceName);
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            logger.LogWarning(ex, "Serviço dependente {Service} não voltou", serviceName);
        }
    }
}
