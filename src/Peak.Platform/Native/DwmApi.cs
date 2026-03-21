using System.Runtime.InteropServices;

namespace Peak.Platform.Native;

public static class DwmApi
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void EnableAcrylic(IntPtr hwnd)
    {
        int value = DWMSBT_TRANSIENTWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
    }

    public static void EnableDarkMode(IntPtr hwnd)
    {
        int value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    public static void EnableRoundedCorners(IntPtr hwnd)
    {
        int value = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }
}
