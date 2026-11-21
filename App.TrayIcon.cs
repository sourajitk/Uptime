using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Uptime
{
    public partial class App
    {
        // Icon rendering
        private static bool IsSystemLightTheme()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int i && i == 1;
            }
            catch
            {
                return false; // Fallback to dark theme if registry read fails
            }
        }

        /// <summary>
        /// Small-icon size at the primary monitor's *current* DPI (where the taskbar notification area lives).
        /// Plain GetSystemMetrics is frozen at the sign-in DPI for per-monitor-aware processes, so it misses scale changes.
        /// </summary>
        private static int GetTrayIconSize()
        {
            int size = 0;
            try
            {
                IntPtr monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0)
                {
                    size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
                }
            }
            catch { }

            if (size <= 0) size = GetSystemMetrics(SM_CXSMICON);
            return size >= 16 ? size : 16;
        }

        private void UpdateTrayIcon(bool showWarning = false, bool force = false)
        {
            bool isLightTheme = IsSystemLightTheme();
            int iconSize = GetTrayIconSize();
            if (!force && _lastIsLightTheme == isLightTheme && _lastShowWarning == showWarning && _lastIconSize == iconSize)
            {
                return;
            }

            _lastIsLightTheme = isLightTheme;
            _lastShowWarning = showWarning;
            _lastIconSize = iconSize;

            IntPtr oldIcon = _iconHandle;
            _iconHandle = CreateClockIcon(isLightTheme, showWarning, iconSize);

            _nid.hIcon = _iconHandle;
            _nid.uFlags = NIF_ICON;
            Shell_NotifyIcon(NIM_MODIFY, ref _nid);

            if (oldIcon != IntPtr.Zero)
            {
                DestroyIcon(oldIcon);
            }
        }

        /// <summary>
        /// Draws the tray icon in a 32x32 design space, scaled to the shell's small-icon size so it stays crisp at any DPI.
        /// The caller owns the returned HICON and must release it with DestroyIcon.
        /// </summary>
        private static IntPtr CreateClockIcon(bool isLightTheme, bool showWarning, int size)
        {
            using var bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.ScaleTransform(size / 32f, size / 32f);

                // 1. Solid Fluent Blue circle (#0078D4)
                using (var fillBrush = new SolidBrush(Color.FromArgb(255, 0, 120, 212)))
                {
                    g.FillEllipse(fillBrush, 2f, 2f, 28f, 28f);
                }

                // 2. White clock hands (9 o'clock hour hand, 12 o'clock minute hand) around the centre (16, 16)
                using (var handPen = new Pen(Color.White, 2.5f))
                {
                    handPen.StartCap = LineCap.Round;
                    handPen.EndCap = LineCap.Round;
                    handPen.LineJoin = LineJoin.Round;

                    g.DrawLine(handPen, 16f, 16f, 8.5f, 16f);
                    g.DrawLine(handPen, 16f, 16f, 16f, 8.5f);
                }

                // If uptime exceeds the threshold, draw a Fluent-style warning badge in the bottom-right corner
                if (showWarning)
                {
                    PointF[] triangle = { new(23f, 14f), new(14f, 30f), new(31f, 30f) };
                    PointF[] borderTriangle = { new(23f, 12f), new(12f, 31.5f), new(32f, 31.5f) };

                    // Cut-out border separating the badge from the clock
                    Color bgCutout = isLightTheme ? Color.White : Color.FromArgb(255, 32, 32, 32);
                    using (var bgBrush = new SolidBrush(bgCutout))
                    {
                        g.FillPolygon(bgBrush, borderTriangle);
                    }

                    // Amber badge (#FFB900) with a dark outline
                    using (var amberBrush = new SolidBrush(Color.FromArgb(255, 255, 185, 0)))
                    {
                        g.FillPolygon(amberBrush, triangle);
                    }
                    using (var outlinePen = new Pen(Color.FromArgb(255, 40, 40, 40), 1f))
                    {
                        g.DrawPolygon(outlinePen, triangle);
                    }

                    // Exclamation mark
                    using (var markPen = new Pen(Color.Black, 2f))
                    {
                        markPen.StartCap = LineCap.Round;
                        markPen.EndCap = LineCap.Round;
                        g.DrawLine(markPen, 22.5f, 18.5f, 22.5f, 23.5f);
                    }
                    g.FillEllipse(Brushes.Black, 21.5f, 25.5f, 2f, 2f);
                }
            }
            return bmp.GetHicon();
        }
    }
}
