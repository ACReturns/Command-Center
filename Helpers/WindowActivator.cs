using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CommandCenter
{
    // Win32 helper behind the Active Clients tab's "Bring to Front" button. Finds the launched
    // client's top-level window (searching the whole process tree, since a launcher exe can hand
    // off to a child process that owns the real game window), restores it if minimized, pulls it
    // onto the same monitor Command Center is on if it's sitting on a different screen (or on a
    // monitor that's no longer connected, i.e. fully off-screen), and activates it.
    public static class WindowActivator
    {
        public enum Result
        {
            Activated,
            ActivatedAndMoved,
            NoWindowFound
        }

        public static Result BringToFront(int rootPid)
        {
            IntPtr target = FindBestWindow(rootPid);
            if (target == IntPtr.Zero)
            {
                return Result.NoWindowFound;
            }

            bool wasMaximized = IsZoomed(target);

            // A minimized window reports a rect of roughly (-32000,-32000), which would read as
            // off-screen below - restore first so the monitor check sees its real position.
            if (IsIconic(target))
            {
                ShowWindow(target, SW_RESTORE);
            }

            bool moved = MoveOntoCommandCenterMonitorIfNeeded(target, wasMaximized);

            Activate(target);
            return moved ? Result.ActivatedAndMoved : Result.Activated;
        }

        private static bool MoveOntoCommandCenterMonitorIfNeeded(IntPtr target, bool wasMaximized)
        {
            Window? mainWindow = Application.Current?.MainWindow;
            if (mainWindow == null)
            {
                return false;
            }

            IntPtr ownHwnd = new WindowInteropHelper(mainWindow).Handle;
            if (ownHwnd == IntPtr.Zero)
            {
                return false;
            }

            IntPtr ownMonitor = MonitorFromWindow(ownHwnd, MONITOR_DEFAULTTONEAREST);
            IntPtr targetMonitor = MonitorFromWindow(target, MONITOR_DEFAULTTONULL);

            // Already on the same screen as Command Center - leave it where the user put it.
            if (targetMonitor != IntPtr.Zero && targetMonitor == ownMonitor)
            {
                return false;
            }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(ownMonitor, ref info) || !GetWindowRect(target, out RECT rect))
            {
                return false;
            }

            // A maximized window can't be repositioned while maximized - restore, move, then
            // re-maximize so it fills Command Center's monitor instead of the old one.
            if (wasMaximized)
            {
                ShowWindow(target, SW_RESTORE);
                GetWindowRect(target, out rect);
            }

            RECT work = info.rcWork;
            int workWidth = work.Right - work.Left;
            int workHeight = work.Bottom - work.Top;
            int width = Math.Min(rect.Right - rect.Left, workWidth);
            int height = Math.Min(rect.Bottom - rect.Top, workHeight);
            int x = work.Left + (workWidth - width) / 2;
            int y = work.Top + (workHeight - height) / 2;

            SetWindowPos(target, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);

            if (wasMaximized)
            {
                ShowWindow(target, SW_MAXIMIZE);
            }

            return true;
        }

        private static void Activate(IntPtr target)
        {
            ShowWindow(target, SW_SHOW);

            // Command Center is the foreground app at this point (the user just clicked its
            // button), so Windows normally allows this. If it's refused anyway, briefly toggling
            // TOPMOST still gets the window visibly on top of everything else.
            if (!SetForegroundWindow(target))
            {
                SetWindowPos(target, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetWindowPos(target, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            }
        }

        // Picks the largest visible, unowned top-level window belonging to the launched process or
        // any of its descendants - that's the main game window in practice, rather than a splash
        // screen, tooltip, or hidden helper window.
        private static IntPtr FindBestWindow(int rootPid)
        {
            HashSet<int> pids = GetProcessTree(rootPid);
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (!pids.Contains((int)pid) || !IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
                {
                    return true;
                }

                // Minimized windows still count (they're exactly what we want to restore) - just
                // rank them as tiny so a real visible window wins if one exists.
                long area = 0;
                if (!IsIconic(hwnd) && GetWindowRect(hwnd, out RECT r))
                {
                    area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                }

                if (area > bestArea)
                {
                    bestArea = area;
                    best = hwnd;
                }

                return true;
            }, IntPtr.Zero);

            return best;
        }

        private static HashSet<int> GetProcessTree(int rootPid)
        {
            var result = new HashSet<int> { rootPid };
            var parentOf = new List<(int Pid, int ParentPid)>();

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
            {
                return result;
            }

            try
            {
                var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                if (Process32First(snapshot, ref entry))
                {
                    do
                    {
                        parentOf.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
                    }
                    while (Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }

            // Keep sweeping until no new descendants turn up (handles grandchildren etc.).
            bool added;
            do
            {
                added = false;
                foreach (var (pid, parent) in parentOf)
                {
                    if (pid != 0 && result.Contains(parent) && result.Add(pid))
                    {
                        added = true;
                    }
                }
            }
            while (added);

            return result;
        }

        #region Win32

        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const int SW_MAXIMIZE = 3;
        private const uint GW_OWNER = 4;
        private const uint MONITOR_DEFAULTTONULL = 0;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new(-2);
        private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        #endregion
    }
}
