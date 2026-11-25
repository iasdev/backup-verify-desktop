using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BackupVerify;

internal sealed record BackupSet(string Name, string NasPath, string UsbPath, string CloudPath, string[] TestFiles)
{
    public override string ToString() => Name;
}

internal sealed record Settings(string UsbVolumeId, string RcloneConfigPath, BackupSet[] BackupSets)
{
    public static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");
    public static string ChecksPath => Path.Combine(AppContext.BaseDirectory, "checks");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Settings Load()
    {
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(ConfigPath), Json)
            ?? throw new InvalidDataException("config.json está vacío.");
        if (settings.BackupSets is not { Length: > 0 } || string.IsNullOrWhiteSpace(settings.RcloneConfigPath))
            throw new InvalidDataException("Configura backupSets y rcloneConfigPath.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in settings.BackupSets)
        {
            if (set is null || !names.Add(Paths.Relative(set.Name)) || set.Name.Contains('/') || set.Name.Contains('\\'))
                throw new InvalidDataException("Cada grupo debe tener un nombre de carpeta único.");
            if (set.NasPath is null || !set.NasPath.StartsWith(@"\\", StringComparison.Ordinal) ||
                set.NasPath.StartsWith(@"\\?", StringComparison.Ordinal) ||
                set.NasPath[2..].TrimEnd('\\').Split('\\').Length < 2 || !Path.IsPathFullyQualified(set.NasPath))
                throw new InvalidDataException($"Configura un recurso SMB en {set.Name}.");
            Paths.Relative(set.UsbPath);
            var colon = set.CloudPath?.IndexOf(':') ?? -1;
            if (colon < 1 || set.CloudPath![0] == ':' || set.CloudPath[..colon].Contains('/') ||
                set.CloudPath[..colon].Contains('\\') || set.TestFiles is null)
                throw new InvalidDataException($"Revisa cloudPath y testFiles en {set.Name}.");
            foreach (var path in set.TestFiles) Paths.Relative(path);
        }
        return settings;
    }

    public void Save()
    {
        var temporary = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
            File.Move(temporary, ConfigPath, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

internal static class Paths
{
    public static string Relative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("Falta una ruta relativa en config.json.");
        var parts = path.Replace('\\', '/').Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0];
            if (part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                      stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                                      "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException($"Ruta relativa no válida: {path}");
        }
        return string.Join('/', parts);
    }

    public static string Local(string root, string relative) =>
        Path.Combine(root, Relative(relative).Replace('/', Path.DirectorySeparatorChar));

    public static string Cloud(string root, string relative) =>
        root.TrimEnd('/') + (root.EndsWith(':') ? "" : "/") + Relative(relative);
}

internal sealed record Volume(string Root, string Label, string Id)
{
    public override string ToString() => string.IsNullOrEmpty(Label) ? Root : $"{Root}  {Label}";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint size);

    private const uint QueryStorageProperty = 0x002D1400;
    private const int BusTypeOffset = 28; // STORAGE_DEVICE_DESCRIPTOR.BusType
    private const uint UsbBusType = 7;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[] input,
        uint inputSize, [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    public static Volume[] Connected()
    {
        var windowsRoot = Path.GetPathRoot(Environment.SystemDirectory);
        return DriveInfo.GetDrives()
            .Where(drive => (drive.DriveType is DriveType.Fixed or DriveType.Removable) &&
                !string.Equals(drive.Name, windowsRoot, StringComparison.OrdinalIgnoreCase))
            .Select(Find).OfType<Volume>().ToArray();
    }

    private static bool IsUsb(string root)
    {
        using var handle = CreateFile(@"\\.\" + root.TrimEnd('\\'), 0,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) return false;

        // The zeroed query selects StorageDeviceProperty and PropertyStandardQuery.
        var query = new byte[12];
        var descriptor = new byte[1024];
        return DeviceIoControl(handle, QueryStorageProperty, query, (uint)query.Length, descriptor,
                   (uint)descriptor.Length, out var returned, IntPtr.Zero) &&
               returned >= BusTypeOffset + sizeof(uint) &&
               BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(BusTypeOffset)) == UsbBusType;
    }

    private static Volume? Find(DriveInfo drive)
    {
        try
        {
            var id = new StringBuilder(128);
            return drive.IsReady && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                && IsUsb(drive.Name) && GetVolumeNameForVolumeMountPoint(drive.Name, id, (uint)id.Capacity)
                ? new Volume(drive.Name, drive.VolumeLabel, id.ToString()) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
