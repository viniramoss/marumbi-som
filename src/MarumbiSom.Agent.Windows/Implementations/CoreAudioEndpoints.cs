using System.Runtime.InteropServices;
using MarumbiSom.Agent.Windows.Abstractions;
using MarumbiSom.Agent.Windows.Interop;
using Microsoft.Extensions.Logging;

namespace MarumbiSom.Agent.Windows.Implementations;

/// <summary>Endpoints de áudio via Core Audio (MMDevice API) e IPolicyConfig.</summary>
public sealed class CoreAudioEndpoints(ILogger<CoreAudioEndpoints> logger) : IAudioEndpoints
{
    public IReadOnlyList<AudioEndpoint> ListRenderEndpoints()
    {
        var enumerator = CreateEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.All, out var collection));
            Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
            var result = new List<AudioEndpoint>((int)count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0)
                {
                    continue;
                }

                try
                {
                    result.Add(Describe(device));
                }
                catch (COMException ex)
                {
                    logger.LogWarning(ex, "Falha ao ler o endpoint de áudio de índice {Index}", i);
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }

            Marshal.ReleaseComObject(collection);
            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public string? GetDefaultRenderEndpointId()
    {
        var enumerator = CreateEnumerator();
        try
        {
            // E_NOTFOUND quando não há nenhuma saída ativa.
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0)
            {
                return null;
            }

            try
            {
                Marshal.ThrowExceptionForHR(device.GetId(out var id));
                return id;
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public void SetDefaultRenderEndpoint(string endpointId)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        var policy = (IPolicyConfig)new PolicyConfigClientComObject();
        try
        {
            foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpointId, role));
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public void SetEndpointVolume(string endpointId, float scalar)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        var level = Math.Clamp(scalar, 0f, 1f);
        WithDevice(endpointId, device =>
        {
            var iid = InterfaceIds.IAudioEndpointVolume;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var obj));
            var volume = (IAudioEndpointVolume)obj;
            try
            {
                var context = Guid.Empty;
                volume.GetMasterVolumeLevelScalar(out var current);
                volume.GetMute(out var muted);
                if (Math.Abs(current - level) > 0.001f)
                {
                    Marshal.ThrowExceptionForHR(volume.SetMasterVolumeLevelScalar(level, ref context));
                }

                if (muted)
                {
                    Marshal.ThrowExceptionForHR(volume.SetMute(false, ref context));
                }
            }
            finally
            {
                Marshal.ReleaseComObject(volume);
            }
        });
    }

    internal static void WithDevice(string endpointId, Action<IMMDevice> action)
    {
        var enumerator = CreateEnumerator();
        try
        {
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(endpointId, out var device));
            try
            {
                action(device);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    private static AudioEndpoint Describe(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var id));
        Marshal.ThrowExceptionForHR(device.GetState(out var state));
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0 /* STGM_READ */, out var store));
        try
        {
            var name = ReadProperty(store, PropertyKey.DeviceFriendlyName, v => v.AsString()) ?? id;
            var container = ReadProperty(store, PropertyKey.DeviceContainerId, v => v.AsGuid());
            return new AudioEndpoint(id, name, container, MapState(state));
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static T? ReadProperty<T>(IPropertyStore store, PropertyKey key, Func<PropVariant, T?> read)
    {
        if (store.GetValue(ref key, out var value) != 0)
        {
            return default;
        }

        try
        {
            return read(value);
        }
        finally
        {
            _ = Ole32.PropVariantClear(ref value);
        }
    }

    private static AudioEndpointState MapState(uint state) => state switch
    {
        DeviceState.Active => AudioEndpointState.Active,
        DeviceState.Disabled => AudioEndpointState.Disabled,
        DeviceState.Unplugged => AudioEndpointState.Unplugged,
        _ => AudioEndpointState.NotPresent,
    };
}
