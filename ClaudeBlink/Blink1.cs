using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ClaudeBlink;

/// <summary>
/// Minimal blink(1) mk2 driver over raw Win32 HID (no dependencies).
/// The device takes 8-byte feature reports with report id 1.
/// </summary>
sealed class Blink1 : IDisposable
{
    const string VidPid = "vid_27b8&pid_01ed";

    SafeFileHandle? _handle;

    public bool IsConnected => _handle is { IsInvalid: false, IsClosed: false };

    /// <summary>Fade to a color over <paramref name="fadeMs"/> milliseconds.
    /// <paramref name="led"/>: 0 = both, 1 = LED 1 (top), 2 = LED 2 (bottom).</summary>
    public bool Fade(Color c, int fadeMs, byte led = 0)
    {
        int t = Math.Clamp(fadeMs / 10, 0, 0xFFFF);
        byte[] report = [1, (byte)'c', c.R, c.G, c.B, (byte)(t >> 8), (byte)(t & 0xFF), led, 0];
        return Send(report);
    }

    public bool Off() => Fade(Color.Black, 0);

    bool Send(byte[] report)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!IsConnected)
            {
                _handle = Open();
                if (_handle is null) return false;
            }
            if (HidD_SetFeature(_handle!, report, report.Length)) return true;
            // Device was probably unplugged; reopen once.
            _handle!.Dispose();
            _handle = null;
        }
        return false;
    }

    static SafeFileHandle? Open()
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == INVALID_HANDLE_VALUE) return null;
        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref iface); i++)
            {
                string? path = GetDevicePath(set, ref iface);
                if (path is null || !path.Contains(VidPid, StringComparison.OrdinalIgnoreCase)) continue;

                var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid)
                {
                    h.Dispose();
                    h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                }
                if (!h.IsInvalid) return h;
                h.Dispose();
            }
            return null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    static string? GetDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int size, IntPtr.Zero);
        if (size <= 0) return null;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on x64, 6 on x86.
            Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, buf, size, out _, IntPtr.Zero)) return null;
            return Marshal.PtrToStringUni(buf + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Dispose() => _handle?.Dispose();

    // ---- Win32 ----
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);

    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
