using System.Runtime.InteropServices;
using System.Text;

namespace RedCompute.App.Services.Hardware;

internal static class NvmlInterop
{
    private const string NvmlLib = "nvml.dll";

    public enum NvmlReturn
    {
        Success = 0,
        Uninitialized = 1,
        InvalidArgument = 2,
        NotSupported = 3,
        NoPermission = 4,
        NotFound = 6,
        InsufficientSize = 7,
        InsufficientPower = 8,
        Unknown = 999
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NvmlProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    public const uint NVML_TEMPERATURE_GPU = 0;
    public const uint NVML_CLOCK_GRAPHICS = 0;
    public const uint NVML_CLOCK_MEM = 2;

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlInit_v2();

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlShutdown();

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetCount_v2(out uint deviceCount);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern NvmlReturn nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetPowerUsage(IntPtr device, out uint power);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint limit);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetClockInfo(IntPtr device, uint clockType, out uint clockMHz);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetComputeRunningProcesses_v3(
        IntPtr device, ref uint infoCount, [Out] NvmlProcessInfo[]? infos);

    [DllImport(NvmlLib, CallingConvention = CallingConvention.Cdecl)]
    public static extern NvmlReturn nvmlDeviceGetGraphicsRunningProcesses_v3(
        IntPtr device, ref uint infoCount, [Out] NvmlProcessInfo[]? infos);
}

internal static class SystemInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
