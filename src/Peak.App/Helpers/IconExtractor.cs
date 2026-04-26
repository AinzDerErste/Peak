using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Peak.App.Helpers;

/// <summary>
/// Extracts an app icon as a frozen <see cref="BitmapSource"/> for any path
/// the Windows Shell understands — file system paths, .lnk shortcuts, and
/// <c>shell:appsFolder\&lt;AppUserModelID&gt;</c> URIs (UWP / Store apps).
///
/// Uses <c>IShellItemImageFactory</c>, the same API Explorer uses, so the
/// returned bitmap matches what Windows itself shows next to the app.
/// </summary>
public static class IconExtractor
{
    /// <summary>Memory cache keyed by parsing path. Bitmaps are frozen so safe to share across threads.</summary>
    private static readonly ConcurrentDictionary<string, BitmapSource> _cache = new();

    /// <summary>
    /// Returns the icon for <paramref name="parsingPath"/> at the requested
    /// pixel size. Cached after first lookup. Returns null on failure (path
    /// invalid, COM call failed, etc.) so callers can fall back gracefully.
    /// </summary>
    public static BitmapSource? GetIcon(string parsingPath, int size = 32)
    {
        if (string.IsNullOrEmpty(parsingPath)) return null;

        // Cache key includes size so 16px and 32px requests are stored independently.
        var cacheKey = $"{parsingPath}|{size}";
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        try
        {
            var iidShellItem = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(parsingPath, IntPtr.Zero, iidShellItem, out var shellItem);
            if (shellItem == null) return null;

            try
            {
                var factory = (IShellItemImageFactory)shellItem;
                var requested = new SIZE { cx = size, cy = size };
                // RESIZETOFIT lets Shell pick the closest cached size and scale —
                // BIGGERSIZEOK avoids ugly upscale of a tiny icon.
                factory.GetImage(requested, SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK, out var hBitmap);
                if (hBitmap == IntPtr.Zero) return null;

                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze(); // safe to use across threads, eligible for GC compaction
                    _cache[cacheKey] = src;
                    return src;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shellItem);
            }
        }
        catch
        {
            // Path didn't resolve or COM call failed — UI shows a blank space.
            return null;
        }
    }

    // ─── Win32 / Shell COM interop ────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }
}
