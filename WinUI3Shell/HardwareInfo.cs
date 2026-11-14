using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinUI3Shell;

/// <summary>读取本机硬件信息：CPU、显卡（注册表）与物理内存。</summary>
internal static class HardwareInfo
{
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string GpuClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>CPU 名称（ProcessorNameString）。</summary>
    public static string CpuName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CpuKey);
            string? name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? "未知" : name.Trim();
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>显卡名称：显卡设备子键 0000、0001… 里的 DriverDesc，取第一个非空的。</summary>
    public static string GpuName()
    {
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(GpuClassKey);
            if (classKey == null)
            {
                return "未知";
            }

            foreach (string sub in classKey.GetSubKeyNames())
            {
                // 显卡实例形如 0000 / 0001；Properties、Configuration 等子键跳过
                if (!int.TryParse(sub, out _))
                {
                    continue;
                }

                using RegistryKey? deviceKey = classKey.OpenSubKey(sub);
                string? desc = deviceKey?.GetValue("DriverDesc") as string;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return desc.Trim();
                }
            }

            return "未知";
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>物理内存 MB：(总量, 可用)。</summary>
    public static (long TotalMb, long AvailMb) Memory()
    {
        try
        {
            var status = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(status))
            {
                return (0, 0);
            }
            return ((long)(status.TotalPhys / 1048576), (long)(status.AvailPhys / 1048576));
        }
        catch
        {
            return (0, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}
