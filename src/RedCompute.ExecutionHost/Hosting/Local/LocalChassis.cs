using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace RedCompute.PluginSdk.Hosting.Local;

/// <summary>
/// What kind of machine this is, as its firmware says: the SMBIOS system enclosure's chassis type
/// (Windows: the raw SMBIOS table, what <c>Win32_SystemEnclosure.ChassisTypes</c> reads; Linux:
/// <c>/sys/class/dmi/id/chassis_type</c>), reduced to a hint: <c>desktop</c>, <c>all-in-one</c>,
/// <c>laptop</c>, <c>tablet</c>, <c>mini</c> or <c>server</c>. Null when the firmware does not say or
/// says something else (a VM often reports "Other"). The kernel turns the hint into a node's default
/// icon; it never overrides one its owner chose.
/// </summary>
internal static class LocalChassis
{
    public const string Desktop = "desktop";
    public const string AllInOne = "all-in-one";
    public const string Laptop = "laptop";
    public const string Tablet = "tablet";
    public const string Mini = "mini";
    public const string Server = "server";

    private static readonly Lazy<string?> Detected = new(Detect);

    /// <summary>This machine's hint, read once.</summary>
    public static string? Current => Detected.Value;

    /// <summary>
    /// The hint for an SMBIOS chassis type (DSP0134, System Enclosure, byte 05h; the lock bit masked off).
    /// </summary>
    public static string? FromSmbiosType(int type) => (type & 0x7F) switch
    {
        3 or 4 or 6 or 7 or 15 or 16 or 24 => Desktop, // desktop, low profile, mini tower, tower, space-saving, lunch box, sealed-case
        13 => AllInOne,
        8 or 9 or 10 or 14 or 31 or 32 => Laptop,      // portable, laptop, notebook, sub notebook, convertible, detachable
        11 or 30 => Tablet,                             // hand held, tablet
        35 or 36 => Mini,                               // mini PC, stick PC
        17 or 23 or 25 or 28 or 29 => Server,           // main server chassis, rack mount, multi-system, blade, blade enclosure
        _ => null,
    };

    /// <summary>
    /// The chassis type in a raw SMBIOS table as <c>GetSystemFirmwareTable('RSMB')</c> returns it: an
    /// 8-byte header (calling method, major, minor, DMI revision, table length) then the structures. The
    /// first type 3 (System Enclosure) structure's byte 05h; null when there is none or the table is cut short.
    /// </summary>
    public static int? ChassisTypeFromRawSmbios(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8) return null;
        var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(raw[4..8]), (uint)(raw.Length - 8));
        var table = raw.Slice(8, length);
        var offset = 0;
        while (offset + 4 <= table.Length)
        {
            var type = table[offset];
            var formatted = table[offset + 1];
            if (formatted < 4 || offset + formatted > table.Length) return null;
            if (type == 3) return formatted > 5 ? table[offset + 5] & 0x7F : null;
            if (type == 127) return null; // end of table
            // The strings follow the formatted area, ended by a double NUL.
            var strings = offset + formatted;
            while (strings + 1 < table.Length && (table[strings] != 0 || table[strings + 1] != 0)) strings++;
            offset = strings + 2;
        }
        return null;
    }

    private static string? Detect()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return FromWindows();
            if (OperatingSystem.IsLinux())
            {
                const string path = "/sys/class/dmi/id/chassis_type";
                return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var type) ? FromSmbiosType(type) : null;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ExternalException or DllNotFoundException
                                          or EntryPointNotFoundException)
        {
            // A hint only: no chassis means the platform's default icon.
        }
        return null;
    }

    private const uint RawSmbiosSignature = 0x52534D42; // 'RSMB'

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId,
        [Out] byte[]? firmwareTableBuffer, uint bufferSize);

    private static string? FromWindows()
    {
        var size = GetSystemFirmwareTable(RawSmbiosSignature, 0, null, 0);
        if (size == 0 || size > 1 << 20) return null;
        var buffer = new byte[size];
        if (GetSystemFirmwareTable(RawSmbiosSignature, 0, buffer, size) != size) return null;
        return ChassisTypeFromRawSmbios(buffer) is { } type ? FromSmbiosType(type) : null;
    }
}
