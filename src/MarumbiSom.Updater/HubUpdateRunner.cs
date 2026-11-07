using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Updater;

/// <summary>
/// Atualização do Hub (D32), rodada a cada minuto pela tarefa agendada "MarumbiSom Hub - atualizador" como SYSTEM.
/// O pedido vem do Hub, que roda com uma conta sem privilégios; por isso nada dele é aceito sem conferir: o pacote é
/// copiado para <c>updater\work</c> e conferido ali (SHA-256, assinatura com a chave de <c>app\</c>, versão mais nova
/// que a instalada), e os arquivos de <c>data\</c> só são lidos sem seguir links (<see cref="SafeFiles"/>). O Updater
/// só grava em <c>app\</c> e <c>updater\</c>. Depois de trocar os arquivos espera o Hub novo gravar
/// <c>confirmed.json</c>; sem isso em 2 minutos devolve a versão anterior (o Hub antigo, ao subir, devolve o banco).
/// A etapa fica em <c>state.json</c>, para uma troca interrompida ser desfeita na execução seguinte.
/// </summary>
public sealed partial class HubUpdateRunner(HubUpdatePaths paths, IServiceControl service, TimeProvider time, Action<string> log)
{
    public static readonly TimeSpan DefaultConfirmTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Pedido mais velho que isto é ignorado (o Hub também desiste dele).</summary>
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(30);

    public const long MaxPackageBytes = 300L * 1024 * 1024;
    private const int MaxSmallFileBytes = 64 * 1024;

    public TimeSpan ConfirmTimeout { get; init; } = DefaultConfirmTimeout;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Trata o pedido pendente ou desfaz uma troca interrompida. Nulo quando não havia nada a fazer.</summary>
    public async Task<HubUpdateResult?> RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Work);
        if (ReadState() is { } interrupted)
        {
            return await RecoverAsync(interrupted, cancellationToken);
        }

        if (!File.Exists(paths.Pending))
        {
            return null;
        }

        byte[] content;
        try
        {
            content = SafeFiles.ReadAllBytes(paths.Pending, paths.Data, MaxSmallFileBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RejectOnce($"erro:{ex.Message}", null, $"O pedido de atualização não pôde ser lido: {ex.Message}");
        }

        HubUpdateRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(content, UpdaterJson.Default.HubUpdateRequest);
        }
        catch (JsonException)
        {
            request = null;
        }

        var key = ValidId(request) ? request!.RequestId : $"sha:{Sha(content)}";
        if (ReadLastRequest() == key)
        {
            return null;
        }

        if (request is null)
        {
            return RejectOnce(key, null, "O pedido de atualização não está no formato esperado.");
        }

        if (Invalid(request) is { } invalid)
        {
            return RejectOnce(key, request, invalid);
        }

        string installed;
        try
        {
            installed = InstalledVersion(paths.App);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return RejectOnce(key, request, $"Não foi possível ler a versão do Hub instalado: {ex.Message}");
        }

        log($"Atualizando o Hub de {installed} para {request.Version} (pedido {request.RequestId}{(request.Automatic ? ", automático" : string.Empty)})");

        // 1. Confere e extrai com o Hub ainda rodando: qualquer problema aqui não derruba o painel.
        if (await PrepareAsync(request, installed, cancellationToken) is { } problem)
        {
            Cleanup();
            return Finish(request.RequestId, request.Version, installed, UpdateStages.Failed, problem);
        }

        // 2. Para o serviço e guarda a versão atual.
        WriteState(request.RequestId, request.Version, installed, HubUpdateStages.Stopping);
        try
        {
            await service.StopAsync(HubUpdatePaths.ServiceName, UpdateRunner.ServiceTimeout, cancellationToken);
            service.KillLeftovers(paths.App);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Não foi possível parar o serviço: {ex.Message}");
            await TryStartAsync(cancellationToken);
            Cleanup();
            return Finish(request.RequestId, request.Version, installed, UpdateStages.Failed, $"Não foi possível parar o serviço do Hub: {ex.Message}");
        }

        try
        {
            if (Directory.Exists(paths.Backup))
            {
                Directory.Delete(paths.Backup, recursive: true);
            }

            UpdateRunner.CopyTree(paths.App, paths.Backup);
            log($"Versão {installed} guardada em {paths.Backup}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Backup falhou: {ex.Message}");
            await TryStartAsync(cancellationToken);
            Cleanup();
            return Finish(request.RequestId, request.Version, installed, UpdateStages.Failed, $"Não foi possível guardar a versão atual: {ex.Message}");
        }

        // 3. Troca os arquivos, inicia e espera o Hub novo confirmar.
        WriteState(request.RequestId, request.Version, installed, HubUpdateStages.Installing);
        string failure;
        try
        {
            UpdateRunner.Mirror(paths.Staging, paths.App);
            WriteState(request.RequestId, request.Version, installed, HubUpdateStages.Confirming);
            log("Arquivos trocados; iniciando o serviço");
            await service.StartAsync(HubUpdatePaths.ServiceName, UpdateRunner.ServiceTimeout, cancellationToken);
            if (await WaitForConfirmationAsync(request.RequestId, request.Version, cancellationToken))
            {
                log($"Versão {request.Version} confirmada pelo Hub");
                Cleanup();
                return Finish(request.RequestId, request.Version, installed, UpdateStages.Done, null);
            }

            failure = $"A versão nova não confirmou em {ConfirmTimeout.TotalMinutes:0} minutos (o Hub não subiu).";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"Falha ao instalar: {ex.Message}";
        }

        // 4. Volta a versão anterior.
        return await RestoreAsync(request.RequestId, request.Version, installed, failure, cancellationToken);
    }

    /// <summary>Versão do <c>MarumbiSom.Hub.dll</c> (o CI gera AssemblyVersion 0.6.41.0 para a 0.6.41).</summary>
    internal static string InstalledVersion(string programDirectory)
    {
        var version = AssemblyName.GetAssemblyName(Path.Combine(programDirectory, HubUpdatePaths.HubDll)).Version
            ?? throw new IOException($"O {HubUpdatePaths.HubDll} não tem versão.");
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    // O JSON pode vir sem campos (nulos apesar do tipo): nada do pedido é usado sem passar por aqui.
    private static string? Invalid(HubUpdateRequest request) =>
        !ValidId(request) ? "O pedido de atualização não tem um identificador válido."
        : !ReleaseSigning.IsVersion(request.Version) ? "O pedido de atualização não tem uma versão válida."
        : request.Sha256 is null || !ShaPattern().IsMatch(request.Sha256) ? "O pedido de atualização não tem um SHA-256 válido."
        : request.Signature is not { Length: > 0 and <= 200 } ? "O pedido de atualização não tem assinatura."
        : null;

    private static bool ValidId(HubUpdateRequest? request) => request?.RequestId is { } id && RequestIdPattern().IsMatch(id);

    private async Task<string?> PrepareAsync(HubUpdateRequest request, string installed, CancellationToken cancellationToken)
    {
        var age = time.GetUtcNow() - request.CreatedAt;
        if (age > MaxRequestAge || age < -TimeSpan.FromMinutes(5))
        {
            return $"O pedido de atualização é de {request.CreatedAt.ToLocalTime():dd/MM HH:mm} e venceu; peça de novo no painel.";
        }

        if (ReleaseSigning.CompareVersions(request.Version, installed) <= 0)
        {
            return $"A versão {request.Version} não é mais nova que a instalada ({installed}).";
        }

        var keyFile = Path.Combine(paths.App, ReleaseSigning.PublicKeyFileName);
        var publicKey = File.Exists(keyFile) ? ReleaseSigning.ReadPublicKey(await File.ReadAllTextAsync(keyFile, cancellationToken)) : null;
        if (publicKey is null)
        {
            return $"O Hub instalado não tem a chave pública das atualizações ({keyFile}); reinstale com o install-hub.ps1.";
        }

        try
        {
            // A cópia em updater\work é a que vale: o Hub não consegue trocá-la depois de conferida.
            string sha;
            await using (var source = SafeFiles.OpenRead(paths.PackagePath(request.Version), paths.Data))
            {
                if (source.Length > MaxPackageBytes)
                {
                    return "O pacote do Hub passa de 300 MB.";
                }

                await using var target = new FileStream(paths.Package, FileMode.Create, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            if (!string.Equals(sha, request.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return "O pacote não é o que o Hub conferiu (SHA-256 diferente).";
            }

            if (!ReleaseSigning.Verify(publicKey, request.Version, sha, request.Signature, ReleaseProducts.Hub))
            {
                return "Assinatura inválida: o pacote não foi assinado pela chave das atualizações do Hub.";
            }

            if (UpdateRunner.Extract(paths.Package, paths.Staging, HubUpdatePaths.HubExe, HubUpdatePaths.HubDll) is { } problem)
            {
                return problem;
            }

            var packaged = InstalledVersion(paths.Staging);
            return packaged == request.Version ? null : $"O pacote assinado como {request.Version} tem o Hub {packaged}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or BadImageFormatException)
        {
            return $"Não foi possível conferir o pacote: {ex.Message}";
        }
    }

    private async Task<HubUpdateResult> RestoreAsync(string requestId, string version, string previous, string failure, CancellationToken cancellationToken)
    {
        WriteState(requestId, version, previous, HubUpdateStages.Restoring);
        log($"{failure} Restaurando a versão {previous}");
        try
        {
            if (!File.Exists(Path.Combine(paths.Backup, HubUpdatePaths.HubExe)))
            {
                throw new IOException($"o backup em {paths.Backup} não está completo");
            }

            await service.StopAsync(HubUpdatePaths.ServiceName, UpdateRunner.ServiceTimeout, cancellationToken);
            service.KillLeftovers(paths.App);
            UpdateRunner.Mirror(paths.Backup, paths.App);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"Restauração falhou: {ex.Message}");
            var result = Finish(requestId, version, previous, UpdateStages.Failed, $"{failure} A restauração também falhou ({ex.Message}); reinstale o Hub com o install-hub.ps1.");
            await TryStartAsync(cancellationToken);
            return result;
        }

        // O resultado vai antes de iniciar: o Hub restaurado procura por ele, antes de abrir o banco, para devolver a cópia.
        Cleanup();
        var rolledBack = Finish(requestId, version, previous, UpdateStages.RolledBack, $"{failure} A versão {previous} foi restaurada, com o banco de antes.");
        await TryStartAsync(cancellationToken);
        return rolledBack;
    }

    private async Task<HubUpdateResult> RecoverAsync(HubUpdateState state, CancellationToken cancellationToken)
    {
        log($"A troca para a versão {state.Version} parou na etapa {state.Stage} (PC desligado ou atualizador encerrado); desfazendo");
        if (state.Stage == HubUpdateStages.Stopping)
        {
            await TryStartAsync(cancellationToken);
            Cleanup();
            return Finish(state.RequestId, state.Version, state.PreviousVersion, UpdateStages.Failed,
                $"A atualização foi interrompida antes de trocar os arquivos; o Hub continua na versão {state.PreviousVersion}.");
        }

        if (state.Stage == HubUpdateStages.Confirming && ReadConfirmation() is { } confirmation
            && confirmation.RequestId == state.RequestId && confirmation.Version == state.Version)
        {
            await TryStartAsync(cancellationToken);
            Cleanup();
            return Finish(state.RequestId, state.Version, state.PreviousVersion, UpdateStages.Done, null);
        }

        return await RestoreAsync(state.RequestId, state.Version, state.PreviousVersion, "A atualização foi interrompida no meio da troca.", cancellationToken);
    }

    private async Task<bool> WaitForConfirmationAsync(string requestId, string version, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        while (time.GetElapsedTime(started) < ConfirmTimeout)
        {
            if (ReadConfirmation() is { } confirmation && confirmation.RequestId == requestId && confirmation.Version == version)
            {
                return true;
            }

            await Task.Delay(PollInterval, time, cancellationToken);
        }

        return ReadConfirmation() is { } last && last.RequestId == requestId && last.Version == version;
    }

    private HubUpdateConfirmation? ReadConfirmation()
    {
        try
        {
            return File.Exists(paths.Confirmed)
                ? JsonSerializer.Deserialize(SafeFiles.ReadAllBytes(paths.Confirmed, paths.Data, MaxSmallFileBytes), UpdaterJson.Default.HubUpdateConfirmation)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // O Hub pode estar gravando o arquivo; a próxima leitura pega.
            return null;
        }
    }

    private HubUpdateState? ReadState()
    {
        if (!File.Exists(paths.State))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(paths.State), UpdaterJson.Default.HubUpdateState);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log($"Etapa anterior ilegível ({ex.Message}); ignorada");
            File.Delete(paths.State);
            return null;
        }
    }

    private void WriteState(string requestId, string version, string previous, string stage) =>
        WriteAtomically(paths.State, JsonSerializer.Serialize(new HubUpdateState(requestId, version, previous, stage, time.GetUtcNow()), UpdaterJson.Default.HubUpdateState));

    private async Task TryStartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await service.StartAsync(HubUpdatePaths.ServiceName, UpdateRunner.ServiceTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A recuperação do serviço (seção 6.1) e o próximo boot ainda podem subir o Hub.
            log($"Não foi possível iniciar o serviço: {ex.Message}");
        }
    }

    /// <summary>Pedido recusado sem tocar no serviço; o mesmo pedido não é tratado (nem registrado) de novo.</summary>
    private HubUpdateResult? RejectOnce(string key, HubUpdateRequest? request, string error)
    {
        if (ReadLastRequest() == key)
        {
            return null;
        }

        var previous = File.Exists(Path.Combine(paths.App, HubUpdatePaths.HubDll)) ? TryInstalledVersion() : string.Empty;
        var requestId = ValidId(request) ? request!.RequestId : string.Empty;
        var version = ReleaseSigning.IsVersion(request?.Version) ? request!.Version : string.Empty;
        return Finish(requestId, version, previous, UpdateStages.Failed, error, key);
    }

    private string TryInstalledVersion()
    {
        try
        {
            return InstalledVersion(paths.App);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private HubUpdateResult Finish(string requestId, string version, string previous, string outcome, string? error, string? key = null)
    {
        var result = new HubUpdateResult(requestId, version, previous, outcome, error, time.GetUtcNow());
        WriteAtomically(paths.Result, JsonSerializer.Serialize(result, UpdaterJson.Default.HubUpdateResult));
        WriteAtomically(paths.LastRequest, key ?? requestId);
        File.Delete(paths.State);
        log($"Resultado: {outcome}{(error is null ? string.Empty : $" ({error})")}");
        return result;
    }

    private void Cleanup()
    {
        try
        {
            File.Delete(paths.Package);
            if (Directory.Exists(paths.Staging))
            {
                Directory.Delete(paths.Staging, recursive: true);
            }
        }
        catch (IOException ex)
        {
            log($"Arquivos temporários não apagados: {ex.Message}");
        }
    }

    private string? ReadLastRequest() => File.Exists(paths.LastRequest) ? File.ReadAllText(paths.LastRequest).Trim() : null;

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaPattern();
}
