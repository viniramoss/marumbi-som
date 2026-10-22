using MarumbiSom.Agent.Audio;
using MarumbiSom.Agent.Bluetooth;
using MarumbiSom.Agent.Configuration;
using MarumbiSom.Agent.Player;
using MarumbiSom.Agent.Poc;
using MarumbiSom.Agent.Simulation;
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
        services.Configure<PocOptions>(configuration.GetSection(PocOptions.Section));
        services.AddSingleton(TimeProvider.System);

        if (simulate)
        {
            AddSimulatedWindows(services);
        }
        else
        {
            AddRealWindows(services);
        }

        services.AddSingleton<BluetoothGuardian>();
        services.AddSingleton<IBluetoothConnection>(sp => sp.GetRequiredService<BluetoothGuardian>());
        services.AddSingleton<SpotDucker>();

        services.AddHostedService<BluetoothGuardianService>();
        services.AddHostedService<AudioEndpointManager>();
        services.AddHostedService<PocPlaybackService>();
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

        services.AddKeyedSingleton<IMpvInstance>(PlayerKeys.Music, (sp, _) => CreateMpv(sp, "música", o => o.MusicPipeName));
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

        services.AddKeyedSingleton<IMpvInstance>(PlayerKeys.Music, (sp, _) =>
            new SimulatedMpvInstance("música", sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(20), sp.GetRequiredService<ILogger<SimulatedMpvInstance>>()));
        services.AddKeyedSingleton<IMpvInstance>(PlayerKeys.Spot, (sp, _) =>
            new SimulatedMpvInstance("ofertas", sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(5), sp.GetRequiredService<ILogger<SimulatedMpvInstance>>()));
    }
}
