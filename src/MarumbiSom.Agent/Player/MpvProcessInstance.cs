using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MarumbiSom.Contracts.Json;

namespace MarumbiSom.Agent.Player;

/// <summary>Processo mpv real, controlado por named pipe com o protocolo JSON IPC do mpv.</summary>
public sealed class MpvProcessInstance(string name, string mpvPath, string pipeName, ILogger<MpvProcessInstance> logger) : IMpvInstance
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readerCts;
    private Task? _reader;
    private int _nextRequestId;
    private bool _stopping;

    public event EventHandler<MpvEventArgs>? EventReceived;

    public event EventHandler? Exited;

    public string Name { get; } = name;

    public bool IsRunning => _process is { HasExited: false } && _pipe is { IsConnected: true };

    public async Task StartAsync(string audioEndpointId, double volume, CancellationToken cancellationToken)
    {
        await StopAsync();
        _stopping = false;
        KillOrphans();

        var info = new ProcessStartInfo(mpvPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        foreach (var argument in BuildArguments(audioEndpointId, volume))
        {
            info.ArgumentList.Add(argument);
        }

        logger.LogInformation("Iniciando mpv '{Name}': {Path} {Arguments}", Name, mpvPath, string.Join(' ', info.ArgumentList));
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += OnProcessExited;
        if (!process.Start())
        {
            throw new InvalidOperationException($"mpv '{Name}' não iniciou.");
        }

        _process = process;
        await ConnectPipeAsync(cancellationToken);

        // Avisos e erros do próprio mpv (ex.: a saída WASAPI caiu) vão para o log do agente como eventos do IPC.
        try
        {
            await CommandAsync(["request_log_messages", "warn"], cancellationToken);
        }
        catch (MpvCommandException ex)
        {
            logger.LogWarning(ex, "mpv '{Name}' não aceitou request_log_messages", Name);
        }
    }

    public async Task StopAsync()
    {
        _stopping = true;
        if (_readerCts is not null)
        {
            await _readerCts.CancelAsync();
        }

        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                logger.LogWarning(ex, "Falha ao encerrar o mpv '{Name}'", Name);
            }
        }

        if (_reader is not null)
        {
            try
            {
                await _reader;
            }
            catch (OperationCanceledException)
            {
                logger.LogDebug("Leitor do IPC do mpv '{Name}' cancelado", Name);
            }
        }

        _writer?.Dispose();
        _pipe?.Dispose();
        _process?.Dispose();
        _readerCts?.Dispose();
        _writer = null;
        _pipe = null;
        _process = null;
        _reader = null;
        _readerCts = null;
        FailPending(new MpvCommandException($"mpv '{Name}' foi parado."));
    }

    public async Task<JsonElement> CommandAsync(IReadOnlyList<object> command, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new MpvCommandException($"mpv '{Name}' não está em execução.");
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var line = JsonSerializer.Serialize(new { command, request_id = id }, ContractJson.Options);
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            return await completion.Task.WaitAsync(CommandTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            throw new MpvCommandException($"Comando '{command[0]}' falhou no mpv '{Name}': {ex.Message}", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeLock.Dispose();
    }

    private IEnumerable<string> BuildArguments(string audioEndpointId, double volume)
    {
        yield return "--idle=yes";
        yield return "--no-video";
        yield return "--no-terminal";
        yield return "--gapless-audio=yes";
        yield return $@"--input-ipc-server=\\.\pipe\{pipeName}";
        yield return "--audio-device=wasapi/" + audioEndpointId;
        yield return "--volume=" + Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture);
        yield return "--volume-max=100";
    }

    private async Task ConnectPipeAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                _pipe = pipe;
                break;
            }
            catch (TimeoutException) when (deadline.Elapsed < ConnectTimeout && _process is { HasExited: false })
            {
                await pipe.DisposeAsync();
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = false, NewLine = "\n" };
        _readerCts = new CancellationTokenSource();
        _reader = Task.Run(() => ReadLoopAsync(_pipe, _readerCts.Token), CancellationToken.None);
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "IPC do mpv '{Name}' encerrado", Name);
                break;
            }

            if (line is null)
            {
                break;
            }

            HandleLine(line);
        }

        FailPending(new MpvCommandException($"IPC do mpv '{Name}' encerrado."));
    }

    private void HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Linha inválida do mpv '{Name}': {Line}", Name, line);
            return;
        }

        using (document)
        {
            var root = document.RootElement.Clone();
            if (root.TryGetProperty("request_id", out var idElement) && idElement.TryGetInt32(out var id) && _pending.TryRemove(id, out var completion))
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() : "success";
                if (error == "success")
                {
                    completion.TrySetResult(root.TryGetProperty("data", out var data) ? data : default);
                }
                else
                {
                    completion.TrySetException(new MpvCommandException($"mpv '{Name}': {error}"));
                }

                return;
            }

            if (root.TryGetProperty("event", out var eventName) && eventName.GetString() is { } name)
            {
                if (name == "log-message")
                {
                    logger.LogWarning(
                        "mpv '{Name}' [{Prefix}] {Level}: {Text}",
                        Name,
                        root.TryGetProperty("prefix", out var prefix) ? prefix.GetString() : null,
                        root.TryGetProperty("level", out var level) ? level.GetString() : null,
                        root.TryGetProperty("text", out var text) ? text.GetString()?.TrimEnd() : null);
                    return;
                }

                try
                {
                    EventReceived?.Invoke(this, new MpvEventArgs(name, root));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Falha ao tratar o evento {Event} do mpv '{Name}'", name, Name);
                }
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = sender is Process p ? p.ExitCode : -1;
        FailPending(new MpvCommandException($"mpv '{Name}' terminou (código {code})."));
        if (_stopping)
        {
            return;
        }

        logger.LogWarning("mpv '{Name}' terminou inesperadamente com código {ExitCode}", Name, code);
        Exited?.Invoke(this, EventArgs.Empty);
    }

    private void FailPending(Exception exception)
    {
        foreach (var (id, completion) in _pending)
        {
            if (_pending.TryRemove(id, out _))
            {
                completion.TrySetException(exception);
            }
        }
    }

    /// <summary>Encerra um mpv órfão de uma execução anterior que ainda esteja usando o mesmo pipe.</summary>
    private void KillOrphans()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'mpv.exe'");
            using var results = searcher.Get();
            foreach (var item in results.Cast<System.Management.ManagementBaseObject>())
            {
                if (item["CommandLine"] is string commandLine && commandLine.Contains(@"\pipe\" + pipeName + " ", StringComparison.OrdinalIgnoreCase)
                    && item["ProcessId"] is uint pid)
                {
                    logger.LogWarning("Encerrando mpv órfão {Pid} que usava o pipe {Pipe}", pid, pipeName);
                    using var orphan = Process.GetProcessById((int)pid);
                    orphan.Kill();
                }
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Falha ao procurar mpv órfão do pipe {Pipe}", pipeName);
        }
    }
}
