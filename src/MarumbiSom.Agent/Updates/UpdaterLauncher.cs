using System.Diagnostics;
using System.Text.Json;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Contracts.Json;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Agent.Updates;

/// <summary>Entrega a instalação ao Updater, com o pedido já gravado em <see cref="UpdatePaths.Pending"/>.</summary>
public interface IUpdaterLauncher
{
    void Launch(string programDirectory, UpdatePaths paths);
}

/// <summary>
/// Copia o <c>MarumbiSom.Updater.exe</c> para <c>data\updates\updater</c> (fora da pasta que ele vai trocar) e o
/// inicia sem janela. Um processo filho comum não é encerrado quando o serviço para, então o Updater continua.
/// </summary>
public sealed class ProcessUpdaterLauncher : IUpdaterLauncher
{
    public void Launch(string programDirectory, UpdatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var source = Path.Combine(programDirectory, UpdateEnvironment.UpdaterFileName);
        if (!File.Exists(source))
        {
            throw new InvalidOperationException($"O agente instalado não tem o {UpdateEnvironment.UpdaterFileName}. Reinstale o agente pelo instalador.");
        }

        Directory.CreateDirectory(paths.UpdaterDirectory);
        var updaterPath = Path.Combine(paths.UpdaterDirectory, UpdateEnvironment.UpdaterFileName);
        File.Copy(source, updaterPath, overwrite: true);
        var info = new ProcessStartInfo(updaterPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updaterPath)!,
        };
        info.ArgumentList.Add("--pending");
        info.ArgumentList.Add(paths.Pending);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("O atualizador não iniciou.");
    }
}

/// <summary>Modo simulado (desenvolvimento e testes ponta a ponta): não troca arquivo nenhum, só registra o resultado.</summary>
public sealed class SimulatedUpdaterLauncher(TimeProvider time, ILogger<SimulatedUpdaterLauncher> logger) : IUpdaterLauncher
{
    public void Launch(string programDirectory, UpdatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(paths.Pending), ContractJson.Options)!;
        logger.LogWarning("Modo simulado: a versão {Version} não é instalada de verdade; o resultado é registrado como concluído", pending.Version);
        var result = new UpdateResult(pending.Version, pending.PreviousVersion, UpdateStages.Done, null, time.GetUtcNow());
        AtomicFile.WriteAllText(paths.Result, JsonSerializer.Serialize(result, ContractJson.Options));
    }
}
