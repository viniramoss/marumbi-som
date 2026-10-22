namespace MarumbiSom.Agent.Bluetooth;

/// <summary>Executa o <see cref="BluetoothGuardian"/> em segundo plano durante toda a vida do serviço.</summary>
public sealed class BluetoothGuardianService(BluetoothGuardian guardian, TimeProvider time, ILogger<BluetoothGuardianService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Guardião Bluetooth iniciado");
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await guardian.StepAsync(stoppingToken);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, time, stoppingToken);
            }
        }
    }
}
