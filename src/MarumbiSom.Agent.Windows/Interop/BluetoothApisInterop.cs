using System.Runtime.InteropServices;

namespace MarumbiSom.Agent.Windows.Interop;

// Declarações de bluetoothapis.h usadas pelo método B da seção 9.3.

[StructLayout(LayoutKind.Sequential)]
internal struct SystemTime
{
    public ushort Year;
    public ushort Month;
    public ushort DayOfWeek;
    public ushort Day;
    public ushort Hour;
    public ushort Minute;
    public ushort Second;
    public ushort Milliseconds;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct BluetoothDeviceInfo
{
    public uint Size;
    public ulong Address;
    public uint ClassOfDevice;
    [MarshalAs(UnmanagedType.Bool)] public bool Connected;
    [MarshalAs(UnmanagedType.Bool)] public bool Remembered;
    [MarshalAs(UnmanagedType.Bool)] public bool Authenticated;
    public SystemTime LastSeen;
    public SystemTime LastUsed;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string Name;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BluetoothFindRadioParams
{
    public uint Size;
}

internal static class BluetoothApis
{
    public const uint BLUETOOTH_SERVICE_DISABLE = 0x00;
    public const uint BLUETOOTH_SERVICE_ENABLE = 0x01;

    /// <summary>AudioSink (A2DP Sink) — UUID 0000110B-0000-1000-8000-00805F9B34FB.</summary>
    public static readonly Guid AudioSinkServiceClass = new("0000110B-0000-1000-8000-00805F9B34FB");

#pragma warning disable CA5392, SYSLIB1054 // Estrutura com string ByValTStr exige marshalling clássico; DLL do sistema
    [DllImport("BluetoothApis.dll", SetLastError = true)]
    public static extern IntPtr BluetoothFindFirstRadio(ref BluetoothFindRadioParams parameters, out IntPtr radio);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BluetoothFindRadioClose(IntPtr find);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    public static extern uint BluetoothGetDeviceInfo(IntPtr radio, ref BluetoothDeviceInfo deviceInfo);

    [DllImport("BluetoothApis.dll", SetLastError = true)]
    public static extern uint BluetoothSetServiceState(IntPtr radio, ref BluetoothDeviceInfo deviceInfo, ref Guid serviceClass, uint serviceFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);
#pragma warning restore CA5392, SYSLIB1054
}
