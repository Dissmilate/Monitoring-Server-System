using ServerMonitor.Protocol.V1;

namespace ServerMonitor.Agent.Metrics;

internal static class DiskUsage
{
    private const int MaxDisks = 32;

    private static readonly HashSet<string> IgnoredFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "autofs", "binfmt_misc", "bpf", "cgroup", "cgroup2", "configfs", "debugfs", "devpts", "devtmpfs",
        "efivarfs", "fusectl", "hugetlbfs", "mqueue", "nsfs", "proc", "pstore", "ramfs", "rpc_pipefs",
        "securityfs", "squashfs", "sysfs", "tmpfs", "tracefs", "nfs", "nfs4", "cifs", "smb3", "9p",
    };

    public static IReadOnlyList<DiskSample> Collect()
    {
        var disks = new List<DiskSample>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!IsRelevant(drive.DriveType, drive.DriveFormat, drive.Name) || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                disks.Add(new DiskSample
                {
                    Mount = drive.Name,
                    TotalBytes = drive.TotalSize,
                    UsedBytes = drive.TotalSize - drive.TotalFreeSpace,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            if (disks.Count == MaxDisks)
            {
                break;
            }
        }

        return disks;
    }

    internal static bool IsRelevant(DriveType type, string format, string mount) =>
        type is DriveType.Fixed or DriveType.Unknown
        && !IgnoredFormats.Contains(format)
        && (OperatingSystem.IsWindows() || Directory.Exists(mount));
}
