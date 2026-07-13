using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace llcom_plus.Tools
{
    class Win32
    {
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            ref int pvAttribute,
            int cbAttribute);

        private const int GWL_STYLE = -16;
        private const int WS_SYSMENU = 0x80000;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWCP_ROUND = 2;
        private const int DWMSBT_NONE = 1;
        private const int DWMSBT_TRANSIENTWINDOW = 3;

        private static bool TryGetColorRef(Window window, string resourceKey, out int colorRef)
        {
            var brush = window.TryFindResource(resourceKey) as SolidColorBrush;
            if (brush == null)
            {
                colorRef = 0;
                return false;
            }

            var color = brush.Color;
            colorRef = color.R | (color.G << 8) | (color.B << 16);
            return true;
        }

        // 隐藏窗口右上角按钮
        internal static bool HideControlBox(IntPtr hWnd)
        {
            return SetWindowLong(hWnd, GWL_STYLE, GetWindowLong(hWnd, GWL_STYLE) & ~WS_SYSMENU) > 0;
        }

        /// <summary>
        /// Applies supported Windows 10/11 non-client theming. Unsupported DWM
        /// attributes simply return an error, so older systems keep the WPF fallback.
        /// </summary>
        internal static void ApplyWindowTheme(Window window, bool darkMode, bool transientBackdrop)
        {
            if (window == null)
                return;

            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                    return;

                var enabled = darkMode ? 1 : 0;
                var size = Marshal.SizeOf(typeof(int));
                if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, size) != 0)
                {
                    DwmSetWindowAttribute(
                        handle,
                        DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1,
                        ref enabled,
                        size);
                }

                var cornerPreference = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, size);

                // The main window paints its own wallpaper and glass hierarchy.  A
                // separate Mica layer in the native caption makes the two areas look
                // disconnected, so only transient dialogs keep a DWM backdrop.
                var backdrop = SystemParameters.HighContrast || !transientBackdrop
                    ? DWMSBT_NONE
                    : DWMSBT_TRANSIENTWINDOW;
                DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, size);

                if (!SystemParameters.HighContrast &&
                    TryGetColorRef(window, "AppTitleBarBrush", out var captionColor))
                {
                    DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref captionColor, size);
                    DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref captionColor, size);
                }

                if (!SystemParameters.HighContrast &&
                    TryGetColorRef(window, "AppGlassTextBrush", out var textColor))
                {
                    DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref textColor, size);
                }
            }
            catch (DllNotFoundException)
            {
                // Pre-DWM systems use the in-app gradient/glass fallback.
            }
            catch (EntryPointNotFoundException)
            {
                // Unsupported Windows versions keep the WPF fallback.
            }
        }
    }
}
