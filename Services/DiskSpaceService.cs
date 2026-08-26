using System.IO;

namespace CommandCenter.Services
{
    // Free space on the drive a build/patch was just extracted to, at the moment it was checked.
    public record DiskSpaceStatus(string DriveLabel, double FreeGigabytes, bool IsLow);

    // Backs the storage tracker at the bottom of the window: checked once at startup (so there's
    // a number to show right away) and again every time a build/patch finishes extracting.
    public static class DiskSpaceService
    {
        public const long LowSpaceThresholdBytes = 70L * 1024 * 1024 * 1024; // 70 GB

        // Returns the current free-space status for the drive containing `path`, or null if the
        // path/drive can't be resolved (e.g. build path not set yet, or drive not ready).
        public static DiskSpaceStatus? CheckDiskSpace(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string? root;
            try
            {
                root = Path.GetPathRoot(Path.GetFullPath(path));
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return null;
                }

                double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                bool isLow = drive.AvailableFreeSpace < LowSpaceThresholdBytes;
                return new DiskSpaceStatus(drive.Name, freeGb, isLow);
            }
            catch
            {
                return null;
            }
        }
    }
}
