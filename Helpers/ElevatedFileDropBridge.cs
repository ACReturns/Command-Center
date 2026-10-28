using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommandCenter.View;
using CommandCenter.ViewModel;

namespace CommandCenter
{
    /// <summary>
    /// Restores file drag-and-drop from Explorer when Command Center is running elevated
    /// ("Run as Administrator" in Settings).
    ///
    /// Why this exists: Windows' UIPI blocks OLE drag-and-drop (what WPF's AllowDrop/Drop events
    /// use) from a normal-integrity process like Explorer into an elevated window - the cursor
    /// shows the "no" symbol and WPF never sees DragEnter/Drop. The legacy WM_DROPFILES channel
    /// CAN be let through, but only if the window (1) revokes its OLE drop target, (2) opts into
    /// WM_DROPFILES via DragAcceptFiles, and (3) allows WM_DROPFILES / WM_COPYDATA /
    /// WM_COPYGLOBALDATA past the UIPI message filter.
    ///
    /// When not elevated this does nothing, so normal WPF drag-drop stays in charge.
    /// Drops land on whichever DocumentsPanelView is under the cursor (BuildSectionView or the
    /// Launcher tab) and go through the same BuildSectionViewModel.ImportDocumentPaths the WPF
    /// Drop handler uses. Drops anywhere else are ignored.
    /// Trade-off while elevated: WPF drag-drop between controls inside the window is disabled
    /// (nothing in the app currently uses it).
    /// </summary>
    internal static class ElevatedFileDropBridge
    {
        private const int WM_DROPFILES = 0x0233;
        private const int WM_COPYDATA = 0x004A;
        private const int WM_COPYGLOBALDATA = 0x0049;
        private const uint MSGFLT_ALLOW = 1;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

        [DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr hwnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        [DllImport("shell32.dll")]
        private static extern bool DragQueryPoint(IntPtr hDrop, out NativePoint lppt);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint lpPoint);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);

        [DllImport("ole32.dll")]
        private static extern int RevokeDragDrop(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        public static void Attach(Window window)
        {
            if (!App.IsRunningElevated())
            {
                return;
            }

            // WPF registers its OLE drop target while the HwndSource is being set up, so do the
            // swap once the handle exists and again after Loaded in case WPF re-registered.
            window.SourceInitialized += (_, _) => Enable(window, hookWndProc: true);
            window.Loaded += (_, _) => Enable(window, hookWndProc: false);
        }

        private static void Enable(Window window, bool hookWndProc)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int revoke = RevokeDragDrop(hwnd); // returns DRAGDROP_E_NOTREGISTERED on the 2nd call - harmless
            ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
            // Process-wide filter too - some Windows builds ignore the per-window one for
            // WM_COPYGLOBALDATA, which is what actually carries the dropped file list.
            ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ALLOW);
            ChangeWindowMessageFilter(WM_COPYDATA, MSGFLT_ALLOW);
            ChangeWindowMessageFilter(WM_COPYGLOBALDATA, MSGFLT_ALLOW);
            DragAcceptFiles(hwnd, true);
            Log($"Elevated drop bridge enabled (hookWndProc={hookWndProc}, revoke=0x{revoke:X8})");

            if (hookWndProc)
            {
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (msg == WM_DROPFILES)
                    {
                        HandleDrop(window, wParam);
                        handled = true;
                    }
                    return IntPtr.Zero;
                });
            }
        }

        private static void HandleDrop(Window window, IntPtr hDrop)
        {
            var paths = new List<string>();
            NativePoint pt;
            bool havePoint;
            try
            {
                uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < count; i++)
                {
                    uint len = DragQueryFile(hDrop, i, null, 0);
                    var sb = new StringBuilder((int)len + 1);
                    DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                    paths.Add(sb.ToString());
                }
                havePoint = DragQueryPoint(hDrop, out pt);
            }
            finally
            {
                DragFinish(hDrop);
            }

            Log($"WM_DROPFILES received: {paths.Count} path(s), point ok={havePoint} client=({pt.X},{pt.Y})");
            foreach (string p in paths)
            {
                Log($"  path: {p}");
            }

            if (paths.Count == 0)
            {
                return;
            }

            try
            {
                // Client pixels -> screen pixels -> window DIPs. Going through the screen lets
                // WPF account for DPI scaling and the WindowChrome/maximized frame offset.
                Point dip;
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (havePoint && ClientToScreen(hwnd, ref pt))
                {
                    dip = window.PointFromScreen(new Point(pt.X, pt.Y));
                }
                else if (GetCursorPos(out NativePoint cursor))
                {
                    dip = window.PointFromScreen(new Point(cursor.X, cursor.Y));
                }
                else
                {
                    Log("  could not resolve drop position");
                    return;
                }

                DocumentsPanelView? panel = FindPanelByHitTest(window, dip) ?? FindPanelByBounds(window, dip);
                Log($"  window point=({dip.X:0},{dip.Y:0}) panel={(panel == null ? "NONE" : "found")} dc={panel?.DataContext?.GetType().Name ?? "null"}");

                if (panel?.DataContext is BuildSectionViewModel vm)
                {
                    Log($"  HasBuildPath={vm.HasBuildPath}");
                    vm.ImportDocumentPaths(paths);
                    Log("  imported");
                }
            }
            catch (Exception ex)
            {
                Log("  ERROR: " + ex);
            }
        }

        private static DocumentsPanelView? FindPanelByHitTest(Window window, Point point)
        {
            DependencyObject? current = window.InputHitTest(point) as DependencyObject;
            while (current != null)
            {
                if (current is DocumentsPanelView panel)
                {
                    return panel;
                }
                current = current is Visual || current is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return null;
        }

        // Fallback: any visible DocumentsPanelView whose on-screen rectangle contains the point.
        private static DocumentsPanelView? FindPanelByBounds(DependencyObject root, Point point)
        {
            Window? window = root as Window ?? Window.GetWindow(root);
            var stack = new Stack<DependencyObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DependencyObject node = stack.Pop();
                if (node is DocumentsPanelView panel && panel.IsVisible && window != null)
                {
                    try
                    {
                        Rect bounds = panel.TransformToAncestor(window)
                            .TransformBounds(new Rect(0, 0, panel.ActualWidth, panel.ActualHeight));
                        if (bounds.Contains(point))
                        {
                            return panel;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // not connected to this window's visual tree - skip
                    }
                }

                int children = VisualTreeHelper.GetChildrenCount(node);
                for (int i = 0; i < children; i++)
                {
                    stack.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
            return null;
        }

        // Writes to %AppData%\...\drop_log.txt (AppPaths.AppDataFolder) so a failed drop can be
        // diagnosed without a debugger. Kept small - trimmed when it passes ~200 KB.
        private static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.AppDataFolder);
                string file = Path.Combine(AppPaths.AppDataFolder, "drop_log.txt");
                if (File.Exists(file) && new FileInfo(file).Length > 200_000)
                {
                    File.Delete(file);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch
            {
                // logging must never break a drop
            }
        }
    }
}
