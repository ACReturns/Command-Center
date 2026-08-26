using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CommandCenter.Helpers
{
    // Colors the native Windows title bar (caption background + text) via the DWM attributes
    // Windows 11 exposes for this, instead of tearing out the whole window chrome and rebuilding
    // drag/resize/minimize/maximize/close/snap by hand. On Windows 10 (or older Windows 11 builds
    // that predate these attributes) the call just fails and the title bar quietly keeps its
    // default color - nothing crashes either way.
    public static class TitleBarColorHelper
    {
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint pvAttribute, int cbAttribute);

        public static void TryApplyTitleBarColors(Window window, Color captionColor, Color textColor)
        {
            void Apply()
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }

                uint caption = ToColorRef(captionColor);
                uint text = ToColorRef(textColor);

                // Ignore the HRESULT - an unsupported OS/build just means the title bar stays default.
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(uint));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(uint));
            }

            // The window handle doesn't exist yet until the window has been shown once. If we're
            // called before that (e.g. from the constructor), wait for SourceInitialized.
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            {
                Apply();
            }
            else
            {
                window.SourceInitialized += (_, _) => Apply();
            }
        }

        // DWM color attributes take a COLORREF (0x00BBGGRR), which is byte-order-reversed from
        // the RGB most people think in.
        private static uint ToColorRef(Color color) =>
            (uint)((color.B << 16) | (color.G << 8) | color.R);
    }
}
