namespace MarumbiSom.Contracts.Releases;

// Arquivos que o agente e o MarumbiSom.Updater trocam em data\updates (D27 e, f). Este arquivo também é compilado
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
