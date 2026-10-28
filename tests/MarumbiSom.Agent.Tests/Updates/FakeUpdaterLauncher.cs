using System.Collections.Concurrent;
using MarumbiSom.Agent.Updates;
using MarumbiSom.Contracts.Releases;

namespace MarumbiSom.Agent.Tests.Updates;

/// <summary>No lugar do MarumbiSom.Updater.exe: só registra que a instalação foi entregue.</summary>
internal sealed class FakeUpdaterLauncher : IUpdaterLauncher
{
    public ConcurrentQueue<string> Launches { get; } = new();

    public Exception? Fail { get; set; }

    public void Launch(string programDirectory, UpdatePaths paths)
    {
        if (Fail is { } fail)
        {
            throw fail;
        }

        Launches.Enqueue(programDirectory);
    }
}
