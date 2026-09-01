using System.IO;

namespace CommandCenter.Services
{
    // Free space on the drive a build/patch was just extracted to, at the moment it was checked.
    public record DiskSpaceStatus(string DriveLabel, double FreeGigabytes, bool IsLow);

    // Backs the storage tracker at the bottom of the window: checked once at startup (so there's
    // a number to show right away) and again every time a build/patch finishes extracting.
    public static class DiskSpaceService
    {
        // "Need more space" cutoff - IsLow flips true here, the tracker's text goes bold/urgent,
        // and this is also the fully-red end of the tracker's green-to-red color gradient.
        public const long LowSpaceThresholdBytes = 60L * 1024 * 1024 * 1024; // 60 GB

        // Free space at/above this is shown fully green ("plenty of space"); the tracker's
        // background/text color gradient runs from here down to LowSpaceThresholdBytes, so the
        // color shift becomes visible well before the hard warning kicks in.
        public const long ComfortableSpaceThresholdBytes = 150L * 1024 * 1024 * 1024; // 150 GB

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
