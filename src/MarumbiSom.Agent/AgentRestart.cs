using System.ServiceProcess;

namespace MarumbiSom.Agent;

/// <summary>
/// Encerra o agente para a recuperação do serviço subir de novo (seção 8.1, "reiniciar agente" do painel).
/// Parar o host não basta: o <c>WindowsServiceLifetime</c> informa ao SCM uma parada limpa (SERVICE_STOPPED com o
/// código do <see cref="ServiceBase"/>, 0) e a recuperação do <c>install-agent.ps1</c> não dispara. Com o código do
/// serviço diferente de zero e <c>failureflag 1</c>, o SCM conta como falha e sobe o agente de novo em 10 s.
/// </summary>
public sealed class AgentRestart
{
    public const int ExitCode = 1;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly Action<int>? _setServiceExitCode;

    public AgentRestart(IHostApplicationLifetime lifetime, IHostLifetime? hostLifetime = null)
        : this(lifetime, hostLifetime is ServiceBase service ? code => service.ExitCode = code : null)
    {
    }

    // Para os testes, que rodam no Linux, onde o ServiceBase não pode ser criado.
    internal AgentRestart(IHostApplicationLifetime lifetime, Action<int>? setServiceExitCode)
    {
        _lifetime = lifetime;
        _setServiceExitCode = setServiceExitCode;
    }

    public bool Requested { get; private set; }

    public void Request()
    {
        Requested = true;
        Environment.ExitCode = ExitCode;
        _setServiceExitCode?.Invoke(ExitCode);
        _lifetime.StopApplication();
    }
}
