using System.Runtime.InteropServices;

namespace Notch.Platform.Native;

public static class User32
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(int hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    public static void SetClickThrough(IntPtr hwnd, bool enable)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (enable)
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT);
        else
            SetWindowLong(hwnd, GWL_EXSTYLE, style & ~WS_EX_TRANSPARENT);
    }

    public static void HideFromAltTab(IntPtr hwnd)
    {
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Checks if a fullscreen application (game, video player etc.) is currently
    /// covering the screen. Compares the foreground window's bounds against its
    /// monitor's full screen area. Ignores the desktop shell (explorer).
    /// </summary>
    public static bool IsFullscreenAppRunning(IntPtr ownHwnd)
    {
        try
        {
            var fgHwnd = GetForegroundWindow();

            // Don't hide for our own window
            if (fgHwnd == IntPtr.Zero || fgHwnd == ownHwnd)
                return false;

            // Ignore the desktop / shell windows
            GetWindowThreadProcessId(fgHwnd, out uint pid);
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                var name = proc.ProcessName.ToLowerInvariant();
                if (name is "explorer" or "searchhost" or "shellexperiencehost" or "startmenuexperiencehost")
                    return false;
            }
            catch { /* process may have exited */ }

            // Get the foreground window bounds
            if (!GetWindowRect(fgHwnd, out RECT windowRect))
                return false;

            // Get the monitor that the foreground window is on
            int monitor = MonitorFromWindow(fgHwnd, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
                return false;

            // Compare: is the foreground window covering the entire monitor?
            var screen = monitorInfo.rcMonitor;
            return windowRect.Left <= screen.Left
                && windowRect.Top <= screen.Top
                && windowRect.Right >= screen.Right
                && windowRect.Bottom >= screen.Bottom;
        }
        catch
        {
            return false;
        }
    }
}
