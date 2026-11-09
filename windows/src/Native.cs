// Windows calls the pet needs: window list and positions, monitor DPI, idle time,
// "is a full-screen app in front", drawing a per-pixel-alpha window, and the audio
// peak meter of the default output device. None of these needs a permission.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TabPet
{
    static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;
        public const int ULW_ALPHA = 2;
        public const byte AC_SRC_ALPHA = 1;
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
                                                      IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        public static extern int mciSendString(string command, StringBuilder ret, int retLength, IntPtr callback);

        public static string ClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // The visible frame of a window (without the invisible resize border), in physical pixels.
        public static bool Bounds(IntPtr hWnd, out RECT r)
        {
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0) return true;
            return GetWindowRect(hWnd, out r);
        }

        // Click-through overlays (game bars, GPU overlays, annotation tools) and floating tool
        // windows are on top of everything but don't really cover anything.
        public static bool IsOverlay(IntPtr hWnd)
        {
            int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
            return (ex & WS_EX_TRANSPARENT) != 0 || ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_NOACTIVATE) != 0);
        }

        // Windows on another virtual desktop, or hidden by the system, are "cloaked".
        public static bool Cloaked(IntPtr hWnd)
        {
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int c, sizeof(int)) == 0 && c != 0;
        }

        // Pixels per 96-dpi unit on the monitor under a point (1.0, 1.25, 1.5 …).
        public static double ScaleAt(int x, int y)
        {
            IntPtr mon = MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST);
            try { if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0) return dx / 96.0; } catch { }
            return 1.0;
        }

        // Seconds since the last keyboard or mouse input.
        public static double SecondsIdle()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
        }

        // A full-screen app (video, game, presentation) is in front.
        public static bool FullScreenAppInFront()
        {
            try
            {
                if (SHQueryUserNotificationState(out int s) != 0) return false;
                return s == 2 || s == 3 || s == 4;   // busy (full screen), D3D full screen, presentation mode
            }
            catch { return false; }
        }
    }

    // Is anything playing through the default output device? Reads its peak meter.
    static class AudioMeter
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
        }

        public static bool SoundPlaying()
        {
            object enumObj = null, meterObj = null; IMMDevice device = null;
            try
            {
                enumObj = new MMDeviceEnumeratorCom();
                var en = (IMMDeviceEnumerator)enumObj;
                if (en.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out device) != 0 || device == null) return false;
                Guid iid = typeof(IAudioMeterInformation).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out meterObj) != 0 || meterObj == null) return false;
                if (((IAudioMeterInformation)meterObj).GetPeakValue(out float peak) != 0) return false;
                return peak > 0.0005f;
            }
            catch { return false; }
            finally
            {
                if (meterObj != null) Marshal.ReleaseComObject(meterObj);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumObj != null) Marshal.ReleaseComObject(enumObj);
            }
        }
    }
}
