using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CommandCenter
{
    // Scans a BuildSection tab's BuildPath folder for .exe files - what feeds Settings' "Available
    // Executables" list (see DraftTabViewModel.RescanExecutables) now that the launch dropdown is
    // no longer limited to the two hard-coded client names in the old LaunchServerCatalog.
    public static class ExecutableScanner
    {
        // Top-level .exe file names found directly in directoryPath (not recursive - the client
        // executables live right in the build root, same place LaunchServerCatalog's hard-coded
        // names always assumed), sorted for a stable, predictable list in the UI. Returns empty
        // rather than throwing for a missing/inaccessible folder - same "just show nothing" fallback
        // BuildSectionViewModel.HasBuildPath already relies on elsewhere.
        public static IReadOnlyList<string> ScanExecutables(string? directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return Array.Empty<string>();
            }

            try
            {
                return Directory.EnumerateFiles(directoryPath, "*.exe", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Select(name => name!)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
