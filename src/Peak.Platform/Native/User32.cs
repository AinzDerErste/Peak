using System.Runtime.InteropServices;

namespace Peak.Platform.Native;

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
    public static bool IsFullscreenAppRunning(IntPtr ownHwnd) =>
        GetForegroundWindowState(ownHwnd).State == ForegroundState.Fullscreen;

    /// <summary>
    /// Returns the foreground window's process name (lower-cased, no ".exe") and
    /// its rough state (Normal / Maximized / Fullscreen). Used by Peak's
    /// TopMost-override logic so user-configured app rules can override the
    /// default fullscreen-hide behaviour.
    /// </summary>
    public static (ForegroundState State, string ProcessName) GetForegroundWindowState(IntPtr ownHwnd)
    {
        try
        {
            var fgHwnd = GetForegroundWindow();
            if (fgHwnd == IntPtr.Zero || fgHwnd == ownHwnd)
                return (ForegroundState.Normal, "");

            GetWindowThreadProcessId(fgHwnd, out uint pid);
            string procName = "";
            try
            {
                var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                procName = proc.ProcessName.ToLowerInvariant();
            }
            catch { /* process may have exited between GetForegroundWindow and now */ }

            // Shell windows aren't "real" foreground apps — treat as Normal.
            if (procName is "explorer" or "searchhost" or "shellexperiencehost" or "startmenuexperiencehost")
                return (ForegroundState.Normal, procName);

            if (!GetWindowRect(fgHwnd, out RECT windowRect))
                return (ForegroundState.Normal, procName);

            int monitor = MonitorFromWindow(fgHwnd, MONITOR_DEFAULTTONEAREST);
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo))
                return (ForegroundState.Normal, procName);

            // Fullscreen: window covers the entire monitor (including taskbar area).
            var screen = monitorInfo.rcMonitor;
            bool fullscreen = windowRect.Left <= screen.Left
                && windowRect.Top <= screen.Top
                && windowRect.Right >= screen.Right
                && windowRect.Bottom >= screen.Bottom;
            if (fullscreen) return (ForegroundState.Fullscreen, procName);

            // Maximized: ask Win32 directly via WindowPlacement.showCmd. Reliable
            // even for borderless apps that match the work-area dimensions.
            var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (GetWindowPlacement(fgHwnd, ref placement) && placement.showCmd == SW_MAXIMIZE)
                return (ForegroundState.Maximized, procName);

            return (ForegroundState.Normal, procName);
        }
        catch
        {
            return (ForegroundState.Normal, "");
        }
    }

    public enum ForegroundState
    {
        Normal,
        Maximized,
        Fullscreen
    }

    private const int SW_MAXIMIZE = 3;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }
}
