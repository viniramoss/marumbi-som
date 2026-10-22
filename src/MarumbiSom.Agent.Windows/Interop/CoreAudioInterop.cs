using System.Runtime.InteropServices;

namespace MarumbiSom.Agent.Windows.Interop;

// Declarações COM do Core Audio (mmdeviceapi.h, devicetopology.h, endpointvolume.h, ks.h).
// A ordem dos métodos reproduz a vtable do SDK do Windows e NÃO pode ser alterada.
#pragma warning disable CA1712 // Prefixos dos valores seguem o SDK do Windows

internal enum EDataFlow
{
    eRender = 0,
    eCapture = 1,
    eAll = 2,
}

internal enum ERole
{
    eConsole = 0,
    eMultimedia = 1,
    eCommunications = 2,
}

internal static class DeviceState
{
    public const uint Active = 0x1;
    public const uint Disabled = 0x2;
    public const uint NotPresent = 0x4;
    public const uint Unplugged = 0x8;
    public const uint All = 0xF;
}

internal static class ClsCtx
{
    public const uint InprocServer = 0x1;
    public const uint All = 0x17;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }

    /// <summary>PKEY_Device_FriendlyName</summary>
    public static PropertyKey DeviceFriendlyName => new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    /// <summary>PKEY_Device_ContainerId</summary>
    public static PropertyKey DeviceContainerId => new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
}

/// <summary>PROPVARIANT reduzido: só os tipos lidos pelo agente (VT_LPWSTR e VT_CLSID).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VT_LPWSTR = 31;
    public const ushort VT_CLSID = 72;

    [FieldOffset(0)]
    public ushort VarType;

    [FieldOffset(8)]
    public IntPtr Pointer;

    public readonly string? AsString() => VarType == VT_LPWSTR && Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(Pointer) : null;

    public readonly Guid? AsGuid() => VarType == VT_CLSID && Pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(Pointer) : null;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KsProperty
{
    public Guid Set;
    public uint Id;
    public uint Flags;
}

internal static class KsConstants
{
    public const uint KSPROPERTY_TYPE_GET = 0x00000001;

    /// <summary>KSPROPSETID_BtAudio (bthdef.h / ksmedia.h)</summary>
    public static readonly Guid KSPROPSETID_BtAudio = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");

    public const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
    public const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
}

internal static class InterfaceIds
{
    public static readonly Guid IDeviceTopology = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    public static readonly Guid IKsControl = new("28F54685-06FD-11D2-B27A-00A0C9223196");
    public static readonly Guid IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object anInterface);

    [PreserveSig]
    int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceTopology
{
    [PreserveSig]
    int GetConnectorCount(out uint count);

    [PreserveSig]
    int GetConnector(uint index, out IConnector connector);

    [PreserveSig]
    int GetSubunitCount(out uint count);

    [PreserveSig]
    int GetSubunit(uint index, out IntPtr subunit);

    [PreserveSig]
    int GetPartById(uint id, out IntPtr part);

    [PreserveSig]
    int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);

    [PreserveSig]
    int GetSignalPath(IntPtr partFrom, IntPtr partTo, [MarshalAs(UnmanagedType.Bool)] bool rejectMixedPaths, out IntPtr parts);
}

[ComImport]
[Guid("9c2c4058-23f5-41de-877a-df3af236a09e")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConnector
{
    [PreserveSig]
    int GetConnectorType(out int type);

    [PreserveSig]
    int GetDataFlow(out int flow);

    [PreserveSig]
    int ConnectTo(IConnector connectTo);

    [PreserveSig]
    int Disconnect();

    [PreserveSig]
    int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);

    [PreserveSig]
    int GetConnectedTo(out IConnector connectedTo);

    [PreserveSig]
    int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string connectorId);

    [PreserveSig]
    int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string deviceId);
}

[ComImport]
[Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPart
{
    [PreserveSig]
    int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);

    [PreserveSig]
    int GetLocalId(out uint id);

    [PreserveSig]
    int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string globalId);

    [PreserveSig]
    int GetPartType(out int partType);

    [PreserveSig]
    int GetSubType(out Guid subType);

    [PreserveSig]
    int GetControlInterfaceCount(out uint count);

    [PreserveSig]
    int GetControlInterface(uint index, out IntPtr controlInterface);

    [PreserveSig]
    int EnumPartsIncoming(out IntPtr parts);

    [PreserveSig]
    int EnumPartsOutgoing(out IntPtr parts);

    [PreserveSig]
    int GetTopologyObject(out IntPtr topology);

    [PreserveSig]
    int Activate(uint clsContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object anInterface);

    [PreserveSig]
    int RegisterControlChangeCallback(ref Guid iid, IntPtr notify);

    [PreserveSig]
    int UnregisterControlChangeCallback(IntPtr notify);
}

[ComImport]
[Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IKsControl
{
    [PreserveSig]
    int KsProperty(ref KsProperty property, uint propertyLength, IntPtr propertyData, uint dataLength, out uint bytesReturned);

    [PreserveSig]
    int KsMethod(IntPtr method, uint methodLength, IntPtr methodData, uint dataLength, out uint bytesReturned);

    [PreserveSig]
    int KsEvent(IntPtr ksEvent, uint eventLength, IntPtr eventData, uint dataLength, out uint bytesReturned);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int GetChannelCount(out uint channelCount);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>CPolicyConfigClient (não documentado). Usado só para restaurar a saída padrão (seção 9.5).</summary>
[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClientComObject
{
}

/// <summary>IPolicyConfig do Windows 7+ (não documentada). Só <see cref="SetDefaultEndpoint"/> é usado.</summary>
[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig]
    int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);

    [PreserveSig]
    int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);

    [PreserveSig]
    int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);

    [PreserveSig]
    int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);

    [PreserveSig]
    int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

    [PreserveSig]
    int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);

    [PreserveSig]
    int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);

    [PreserveSig]
    int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
}

internal static partial class Ole32
{
    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PropVariant value);
}
#pragma warning restore CA1712
