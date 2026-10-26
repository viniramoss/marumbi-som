namespace MarumbiSom.Contracts.Releases;

// Arquivos que o agente (D27 e, f) e o Hub (D32) trocam com o MarumbiSom.Updater. Este arquivo também é compilado
// dentro do Updater (link no .csproj), que não referencia o Contracts para continuar pequeno e sem dependências.

/// <summary>Etapas da atualização: as três primeiras no <c>status</c>, as outras como resultado (eventos <c>update.*</c>).</summary>
public static class UpdateStages
{
    public const string Downloading = "downloading";
    public const string Verifying = "verifying";
    public const string Installing = "installing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string RolledBack = "rolledBack";

    public static bool InProgress(string stage) => stage is Downloading or Verifying or Installing;
}

/// <summary>Pastas e arquivos de <c>data\updates</c>.</summary>
public sealed class UpdatePaths(string dataRoot)
{
    public string Root { get; } = Path.Combine(dataRoot, "updates");

    /// <summary>Pacote baixado pelo agente (<c>.part</c> enquanto baixa).</summary>
    public string DownloadDirectory => Path.Combine(Root, "download");

    /// <summary>Cópia do Updater, que roda de fora da pasta do programa para poder trocá-la.</summary>
    public string UpdaterDirectory => Path.Combine(Root, "updater");

    public string Staging => Path.Combine(Root, "staging");

    public string Backup => Path.Combine(Root, "backup");

    /// <summary>Pedido do agente ao Updater.</summary>
    public string Pending => Path.Combine(Root, "pending.json");

    /// <summary>Gravado pelo agente novo logo depois de mandar o hello ao Hub.</summary>
    public string Confirmed => Path.Combine(Root, "confirmed.json");

    /// <summary>Resultado gravado pelo Updater; o agente que subir manda ao Hub e apaga.</summary>
    public string Result => Path.Combine(Root, "result.json");

    public string Log => Path.Combine(Root, "updater.log");

    public string PackagePath(string version) => Path.Combine(DownloadDirectory, $"MarumbiSom.Agent-{version}.zip");
}

/// <param name="Version">Versão que está sendo instalada.</param>
/// <param name="PreviousVersion">Versão em execução quando o pedido foi feito.</param>
/// <param name="Sha256">SHA-256 do pacote já conferido pelo agente; o Updater confere de novo antes de extrair.</param>
/// <param name="ProgramDirectory">Pasta do agente (C:\Program Files\MarumbiSom).</param>
public sealed record PendingUpdate(
    string Version, string PreviousVersion, string PackagePath, string Sha256, string ProgramDirectory, string ServiceName, DateTimeOffset CreatedAt);

public sealed record UpdateConfirmation(string Version, DateTimeOffset At);

/// <param name="Outcome">Um de <see cref="UpdateStages.Done"/>, <see cref="UpdateStages.Failed"/> ou <see cref="UpdateStages.RolledBack"/>.</param>
public sealed record UpdateResult(string Version, string PreviousVersion, string Outcome, string? Error, DateTimeOffset At);

/// <summary>
/// Pastas e arquivos da atualização do Hub (D32), a partir da raiz <c>C:\MarumbiSom\Hub</c>. O Hub (conta do serviço)
/// grava só em <c>data\updates</c>; o Updater (tarefa agendada como SYSTEM) grava só em <c>app\</c> e
/// <c>updater\</c>, onde a conta do serviço só lê.
/// </summary>
public sealed class HubUpdatePaths(string root)
{
    public const string ServiceName = "MarumbiSom.Hub";
    public const string HubExe = "MarumbiSom.Hub.exe";
    public const string HubDll = "MarumbiSom.Hub.dll";

    public string Root { get; } = Path.GetFullPath(root);

    public string App => Path.Combine(Root, "app");

    public string Data => Path.Combine(Root, "data");

    /// <summary>Pasta do Hub: pacote baixado, pedido, confirmação e cópia do banco.</summary>
    public string Updates => Path.Combine(Data, "updates");

    public string DownloadDirectory => Path.Combine(Updates, "download");

    /// <summary>Pedido do Hub ao Updater.</summary>
    public string Pending => Path.Combine(Updates, "pending.json");

    /// <summary>Gravado pelo Hub novo depois de subir.</summary>
    public string Confirmed => Path.Combine(Updates, "confirmed.json");

    /// <summary>Cópia do banco feita pelo Hub novo antes de abrir o banco; o Hub antigo devolve se a versão voltar.</summary>
    public string DatabaseBackups => Path.Combine(Updates, "banco-antes");

    /// <summary>Pasta do Updater: a cópia do exe que a tarefa roda, o trabalho, o resultado e o log.</summary>
    public string Updater => Path.Combine(Root, "updater");

    public string Work => Path.Combine(Updater, "work");

    /// <summary>Cópia do pacote conferida pelo Updater (o Hub não consegue trocar depois de conferida).</summary>
    public string Package => Path.Combine(Work, "package.zip");

    public string Staging => Path.Combine(Work, "staging");

    /// <summary>A versão anterior de <c>app\</c>, para a volta automática.</summary>
    public string Backup => Path.Combine(Work, "backup");

    /// <summary>SHA-256 do último <c>pending.json</c> tratado, para a tarefa não repetir o mesmo pedido a cada minuto.</summary>
    public string LastRequest => Path.Combine(Work, "last-request.txt");

    /// <summary>Etapa da troca em andamento; se a energia cair no meio, a próxima execução desfaz.</summary>
    public string State => Path.Combine(Updater, "state.json");

    public string Result => Path.Combine(Updater, "result.json");

    public string Log => Path.Combine(Updater, "hub-updater.log");

    public string UpdaterExe => Path.Combine(Updater, "MarumbiSom.Updater.exe");

    public string PackagePath(string version) => Path.Combine(DownloadDirectory, $"MarumbiSom.Hub-{version}.zip");

    public string SignaturePath(string version) => Path.Combine(DownloadDirectory, $"MarumbiSom.Hub-{version}.sig.json");

    public string DatabaseBackup(string requestId) => Path.Combine(DatabaseBackups, requestId);
}

/// <summary>Etapas da troca do Hub em <c>updater\state.json</c> (D32 e).</summary>
public static class HubUpdateStages
{
    /// <summary>Serviço parando e <c>app\</c> sendo guardado; os arquivos ainda são os da versão anterior.</summary>
    public const string Stopping = "stopping";

    /// <summary>Backup completo, arquivos sendo trocados.</summary>
    public const string Installing = "installing";

    /// <summary>Versão nova iniciada, esperando a confirmação.</summary>
    public const string Confirming = "confirming";

    /// <summary>Voltando a versão anterior.</summary>
    public const string Restoring = "restoring";
}

/// <param name="RequestId">32 caracteres hexadecimais, novo a cada pedido.</param>
/// <param name="Sha256">SHA-256 do pacote em <c>data\updates\download</c>; o Updater confere de novo numa cópia própria.</param>
/// <param name="Automatic">Pedido pela atualização da madrugada (não pelo botão).</param>
public sealed record HubUpdateRequest(
    string RequestId, string Version, string PreviousVersion, string Sha256, string Signature, DateTimeOffset CreatedAt, bool Automatic);

/// <param name="Stage">Um de <see cref="HubUpdateStages"/>.</param>
public sealed record HubUpdateState(string RequestId, string Version, string PreviousVersion, string Stage, DateTimeOffset At);

public sealed record HubUpdateConfirmation(string RequestId, string Version, DateTimeOffset At);

/// <param name="RequestId">Do pedido tratado; vazio quando o <c>pending.json</c> nem pôde ser lido.</param>
/// <param name="Outcome">Um de <see cref="UpdateStages.Done"/>, <see cref="UpdateStages.Failed"/> ou <see cref="UpdateStages.RolledBack"/>.</param>
public sealed record HubUpdateResult(string RequestId, string Version, string PreviousVersion, string Outcome, string? Error, DateTimeOffset At);
