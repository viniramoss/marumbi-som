namespace MarumbiSom.Agent.Windows.Abstractions;

/// <summary>Serviços do Windows usados pelo agente (<c>bthserv</c>, <c>Audiosrv</c>).</summary>
public interface ISystemServices
{
    bool IsRunning(string serviceName);

    Task StartAsync(string serviceName, CancellationToken cancellationToken);

    /// <summary>Para (com dependentes) e inicia novamente o serviço (nível 2 da seção 9.2).</summary>
    Task RestartAsync(string serviceName, CancellationToken cancellationToken);
}

public static class WindowsServiceNames
{
    public const string Bluetooth = "bthserv";
    public const string Audio = "Audiosrv";
}
