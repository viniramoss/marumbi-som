using MarumbiSom.Agent.Audio;
using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Configuration;
using MarumbiSom.Agent.Playback;
using MarumbiSom.Agent.Player;
using MarumbiSom.Agent.Simulation;
using MarumbiSom.Agent.Spots;
using MarumbiSom.Agent.Storage;
using MarumbiSom.Agent.Windows.Abstractions;
using MarumbiSom.Agent.Windows.Implementations;
using Microsoft.Extensions.Options;

namespace MarumbiSom.Agent;

public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddMarumbiAgent(this IServiceCollection services, IConfiguration configuration, bool simulate)
    {
        services.Configure<BluetoothOptions>(configuration.GetSection(BluetoothOptions.Section));
        services.Configure<PlayerOptions>(configuration.GetSection(PlayerOptions.Section));
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        services.AddSingleton(TimeProvider.System);

        if (simulate)
        {
            AddSimulatedWindows(services);
        }
        else
        {
            AddRealWindows(services);
        }

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentOptions>>().Value.DataDirectory is { Length: > 0 } directory
            ? new AgentDataPaths(directory)
            : AgentDataPaths.Default);
        services.AddSingleton<ManifestStore>();
        services.AddSingleton<CacheManager>();
        services.AddSingleton<StateStore>();
        services.AddSingleton(new PlaylistOrder(Random.Shared));
        services.AddSingleton<IPlaybackEvents, PlaybackEventLog>();
        services.AddSingleton<SpotScheduler>();
        services.AddSingleton(sp => new PlayerEngine(
            sp.GetRequiredKeyedService<IMpvInstance>(PlayerKeys.MusicA),
            sp.GetRequiredKeyedService<IMpvInstance>(PlayerKeys.MusicB),
            sp.GetRequiredKeyedService<IMpvInstance>(PlayerKeys.Spot),
            sp.GetRequiredService<IBluetoothConnection>(),
            sp.GetRequiredService<ManifestStore>(),
            sp.GetRequiredService<CacheManager>(),
            sp.GetRequiredService<StateStore>(),
            sp.GetRequiredService<PlaylistOrder>(),
            sp.GetRequiredService<SpotScheduler>(),
            sp.GetRequiredService<IPlaybackEvents>(),
            sp.GetRequiredService<AgentDataPaths>().Root,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<PlayerEngine>>()));

        services.AddSingleton<BluetoothGuardian>();
        services.AddSingleton<IBluetoothConnection>(sp => sp.GetRequiredService<BluetoothGuardian>());

        // A ordem importa: o armazenamento carrega estado e manifestos antes do player começar.
        services.AddHostedService<StorageService>();
        services.AddHostedService<BluetoothGuardianService>();
        services.AddHostedService<AudioEndpointManager>();
        services.AddHostedService<PlayerService>();
        return services;
    }

    private static void AddRealWindows(IServiceCollection services)
    {
        services.AddSingleton<IBluetoothRadio, WinRtBluetoothRadio>();
        services.AddSingleton<ISystemServices, WindowsSystemServices>();
        services.AddSingleton<IBluetoothSpeakerControl, WindowsBluetoothSpeakerControl>();
        services.AddSingleton<IAudioEndpoints, CoreAudioEndpoints>();
        services.AddSingleton<IBluetoothAdapterControl>(sp => new PnpBluetoothAdapterControl(
            sp.GetRequiredService<ILogger<PnpBluetoothAdapterControl>>(),
            sp.GetRequiredService<IOptions<BluetoothOptions>>().Value.AdapterInstanceId));

        services.AddKeyedSingleton<IMpvInstance>(PlayerKeys.MusicA, (sp, _) => CreateMpv(sp, "música A", o => o.MusicAPipeName));
        services.AddKeyedSingleton<IMpvInstance>(PlayerKeys.MusicB, (sp, _) => CreateMpv(sp, "música B", o => o.MusicBPipeName));
        services.AddKeyedSingleton<IMpvInstance>(PlayerKeys.Spot, (sp, _) => CreateMpv(sp, "ofertas", o => o.SpotPipeName));
    }

    private static MpvProcessInstance CreateMpv(IServiceProvider sp, string name, Func<PlayerOptions, string> pipe)
    {
        var options = sp.GetRequiredService<IOptions<PlayerOptions>>().Value;
        return new MpvProcessInstance(name, options.ResolveMpvPath(), pipe(options), sp.GetRequiredService<ILogger<MpvProcessInstance>>());
    }

    private static void AddSimulatedWindows(IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var address = sp.GetRequiredService<IOptions<BluetoothOptions>>().Value.SpeakerAddress;
            return new SimulatedWindowsEnvironment(BluetoothAddress.TryParse(address, out var parsed) ? parsed : BluetoothAddress.Parse("00:11:22:33:44:55"));
        });
        services.AddSingleton<IBluetoothRadio>(sp => sp.GetRequiredService<SimulatedWindowsEnvironment>());
        services.AddSingleton<ISystemServices>(sp => sp.GetRequiredService<SimulatedWindowsEnvironment>());
        services.AddSingleton<IBluetoothAdapterControl>(sp => sp.GetRequiredService<SimulatedWindowsEnvironment>());
        services.AddSingleton<IBluetoothSpeakerControl>(sp => sp.GetRequiredService<SimulatedWindowsEnvironment>());
        services.AddSingleton<IAudioEndpoints>(sp => sp.GetRequiredService<SimulatedWindowsEnvironment>());

        foreach (var (key, name) in new[] { (PlayerKeys.MusicA, "música A"), (PlayerKeys.MusicB, "música B"), (PlayerKeys.Spot, "ofertas") })
        {
            services.AddKeyedSingleton<IMpvInstance>(key, (sp, _) => new SimulatedMpvInstance(
                name, sp.GetRequiredService<TimeProvider>(), path => SimulatedDuration(sp, path), sp.GetRequiredService<ILogger<SimulatedMpvInstance>>()));
        }
    }

    /// <summary>No modo simulado a duração vem do manifesto ativo (ou 20 s quando desconhecida).</summary>
    private static TimeSpan SimulatedDuration(IServiceProvider sp, string path)
    {
        var hash = Path.GetFileNameWithoutExtension(path);
        var manifest = sp.GetRequiredService<ManifestStore>().Active;
        return manifest?.Tracks.TryGetValue(hash, out var info) == true && info.DurationMs > 0
            ? TimeSpan.FromMilliseconds(info.DurationMs)
            : TimeSpan.FromSeconds(20);
    }
}
