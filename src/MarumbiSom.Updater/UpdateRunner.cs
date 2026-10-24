using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Updater;

/// <summary>Controle do serviço do agente; nos testes, um falso que simula o agente novo subindo (ou não).</summary>
public interface IServiceControl
{
    Task StopAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken);

    Task StartAsync(string serviceName, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Encerra processos que ainda rodam da pasta do programa (agente ou mpv) e prenderiam os arquivos.</summary>
    void KillLeftovers(string programDirectory);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PendingUpdate))]
[JsonSerializable(typeof(UpdateConfirmation))]
[JsonSerializable(typeof(UpdateResult))]
internal sealed partial class UpdaterJson : JsonSerializerContext;

/// <summary>
/// Instalação de uma versão nova do agente (seção 12.2, D27 f): confere o pacote, extrai para o staging (recusando
/// caminhos que saiam da pasta), para o serviço, guarda a versão atual no backup, troca os arquivos e inicia o serviço.
/// Se o agente novo não gravar <c>confirmed.json</c> (o que ele faz depois do hello ao Hub) em 2 minutos, restaura o
/// backup e inicia de novo. O resultado fica em <c>result.json</c> para o agente que subir mandar ao Hub.
/// </summary>
public sealed class UpdateRunner(UpdatePaths paths, IServiceControl service, TimeProvider time, Action<string> log)
{
    public static readonly TimeSpan DefaultConfirmTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);
    public const string AgentExe = "MarumbiSom.Agent.exe";

    public TimeSpan ConfirmTimeout { get; init; } = DefaultConfirmTimeout;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<UpdateResult?> RunAsync(CancellationToken cancellationToken)
    {
        PendingUpdate? pending;
        try
        {
            pending = JsonSerializer.Deserialize(await File.ReadAllTextAsync(paths.Pending, cancellationToken), UpdaterJson.Default.PendingUpdate);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log($"Pedido de atualização ilegível ({paths.Pending}): {ex.Message}");
            return null;
        }

        if (pending is null)
        {
            return null;
        }

        log($"Atualizando o agente de {pending.PreviousVersion} para {pending.Version} em {pending.ProgramDirectory}");
        var program = Path.GetFullPath(pending.ProgramDirectory);

        // 1. Pacote e staging, com o serviço ainda rodando: qualquer problema aqui não para a loja.
        if (await PrepareAsync(pending, cancellationToken) is { } problem)
        {
            return Finish(pending, UpdateStages.Failed, problem);
        }

        File.Delete(paths.Confirmed);

        // 2. Para o serviço e guarda a versão atual.
        try
        {
            await service.StopAsync(pending.ServiceName, ServiceTimeout, cancellationToken);
            service.KillLeftovers(program);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Não foi possível parar o serviço: {ex.Message}");
            await TryStartAsync(pending, cancellationToken);
            return Finish(pending, UpdateStages.Failed, $"Não foi possível parar o serviço do agente: {ex.Message}");
        }

        try
        {
            if (Directory.Exists(paths.Backup))
            {
                Directory.Delete(paths.Backup, recursive: true);
            }

            CopyTree(program, paths.Backup);
            log($"Versão {pending.PreviousVersion} guardada em {paths.Backup}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Backup falhou: {ex.Message}");
            await TryStartAsync(pending, cancellationToken);
            return Finish(pending, UpdateStages.Failed, $"Não foi possível guardar a versão atual: {ex.Message}");
        }

        // 3. Troca os arquivos, inicia e espera o agente novo confirmar.
        string? failure = null;
        try
        {
            Mirror(paths.Staging, program);
            log("Arquivos trocados; iniciando o serviço");
            await service.StartAsync(pending.ServiceName, ServiceTimeout, cancellationToken);
            if (await WaitForConfirmationAsync(pending.Version, cancellationToken))
            {
                log($"Versão {pending.Version} confirmada pelo agente");
                File.Delete(pending.PackagePath);
                return Finish(pending, UpdateStages.Done, null);
            }

            failure = $"A versão nova não confirmou em {ConfirmTimeout.TotalMinutes:0} minutos (não conversou com o Hub).";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"Falha ao instalar: {ex.Message}";
        }

        // 4. Volta a versão anterior.
        log($"{failure} Restaurando a versão {pending.PreviousVersion}");
        try
        {
            await service.StopAsync(pending.ServiceName, ServiceTimeout, cancellationToken);
            service.KillLeftovers(program);
            Mirror(paths.Backup, program);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Restauração falhou: {ex.Message}");
            var result = Finish(pending, UpdateStages.Failed, $"{failure} A restauração também falhou ({ex.Message}); reinstale o agente pelo instalador.");
            await TryStartAsync(pending, cancellationToken);
            return result;
        }

        // O resultado vai antes de iniciar, para o agente restaurado já encontrar ao subir.
        var rolledBack = Finish(pending, UpdateStages.RolledBack, $"{failure} A versão {pending.PreviousVersion} foi restaurada.");
        await TryStartAsync(pending, cancellationToken);
        return rolledBack;
    }

    /// <summary>Confere o SHA-256 do pacote e extrai para o staging. Devolve o problema, ou nulo.</summary>
    private async Task<string?> PrepareAsync(PendingUpdate pending, CancellationToken cancellationToken)
    {
        try
        {
            string sha;
            await using (var stream = File.OpenRead(pending.PackagePath))
            {
                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            }

            if (!string.Equals(sha, pending.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return "O pacote mudou depois de conferido pelo agente (SHA-256 diferente).";
            }

            if (Directory.Exists(paths.Staging))
            {
                Directory.Delete(paths.Staging, recursive: true);
            }

            Directory.CreateDirectory(paths.Staging);
            var root = Path.GetFullPath(paths.Staging) + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(pending.PackagePath);
            foreach (var entry in zip.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return $"O pacote tem um caminho que sai da pasta ({entry.FullName}).";
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }

            return File.Exists(Path.Combine(paths.Staging, AgentExe)) ? null : $"O pacote não tem o {AgentExe}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"Não foi possível abrir o pacote: {ex.Message}";
        }
    }

    private async Task<bool> WaitForConfirmationAsync(string version, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        while (time.GetElapsedTime(started) < ConfirmTimeout)
        {
            if (ReadConfirmation() is { } confirmation && confirmation.Version == version)
            {
                return true;
            }

            await Task.Delay(PollInterval, time, cancellationToken);
        }

        return ReadConfirmation()?.Version == version;
    }

    private UpdateConfirmation? ReadConfirmation()
    {
        try
        {
            return File.Exists(paths.Confirmed)
                ? JsonSerializer.Deserialize(File.ReadAllText(paths.Confirmed), UpdaterJson.Default.UpdateConfirmation)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // O agente pode estar gravando o arquivo; a próxima leitura pega.
            return null;
        }
    }

    private async Task TryStartAsync(PendingUpdate pending, CancellationToken cancellationToken)
    {
        try
        {
            await service.StartAsync(pending.ServiceName, ServiceTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A recuperação do serviço (seção 8.1) e o próximo boot ainda podem subir o agente.
            log($"Não foi possível iniciar o serviço: {ex.Message}");
        }
    }

    private UpdateResult Finish(PendingUpdate pending, string outcome, string? error)
    {
        var result = new UpdateResult(pending.Version, pending.PreviousVersion, outcome, error, time.GetUtcNow());
        var temp = paths.Result + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, UpdaterJson.Default.UpdateResult), new UTF8Encoding(false));
        File.Move(temp, paths.Result, overwrite: true);
        File.Delete(paths.Pending);
        File.Delete(paths.Confirmed);
        try
        {
            if (Directory.Exists(paths.Staging))
            {
                Directory.Delete(paths.Staging, recursive: true);
            }
        }
        catch (IOException ex)
        {
            log($"Staging não apagado: {ex.Message}");
        }

        log($"Resultado: {outcome}{(error is null ? string.Empty : $" ({error})")}");
        return result;
    }

    /// <summary>
    /// Deixa <paramref name="target"/> igual a <paramref name="source"/>. Pastas da raiz que o pacote não traz (ex.:
    /// <c>tools\</c> instalado à parte) ficam como estão.
    /// </summary>
    internal static void Mirror(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(target))
        {
            if (!File.Exists(Path.Combine(source, Path.GetFileName(file))))
            {
                File.Delete(file);
            }
        }

        foreach (var directory in Directory.GetDirectories(target))
        {
            var name = Path.GetFileName(directory);
            if (Directory.Exists(Path.Combine(source, name)))
            {
                DeleteMissing(Path.Combine(source, name), directory);
            }
        }

        CopyTree(source, target);
    }

    private static void DeleteMissing(string source, string target)
    {
        foreach (var file in Directory.GetFiles(target))
        {
            if (!File.Exists(Path.Combine(source, Path.GetFileName(file))))
            {
                File.Delete(file);
            }
        }

        foreach (var directory in Directory.GetDirectories(target))
        {
            var match = Path.Combine(source, Path.GetFileName(directory));
            if (Directory.Exists(match))
            {
                DeleteMissing(match, directory);
            }
            else
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    internal static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
