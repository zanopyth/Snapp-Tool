using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTool.Capture;

internal static class CaptureService
{
    public static Rectangle GetVirtualScreenBounds() => SystemInformation.VirtualScreen;

    public static Bitmap CaptureAllScreens() => CaptureRect(GetVirtualScreenBounds());

    public static Bitmap CaptureRect(Rectangle bounds, bool includeCursor = false)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        if (includeCursor) DrawCursor(g, bounds);
        return bmp;
    }

    // Graphics.CopyFromScreen only copies window content — the OS composites the mouse pointer
    // separately, so it never shows up in a plain screen copy. Drawing it back in needs the raw
    // Win32 handle (GetCursorInfo) rather than System.Windows.Forms.Cursor, which only reflects
    // whatever cursor this app's own message loop last set, not the true system-wide pointer.
    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || info.flags != CURSOR_SHOWING || info.hCursor == IntPtr.Zero) return;
        if (!GetIconInfo(info.hCursor, out var iconInfo)) return;

        try
        {
            int x = info.ptScreenPos.X - bounds.X - iconInfo.xHotspot;
            int y = info.ptScreenPos.Y - bounds.Y - iconInfo.yHotspot;

            IntPtr hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        finally
        {
            // These bitmap handles are copies GetIconInfo allocated for us — unlike hCursor itself
            // (a shared system resource that must NOT be destroyed), these are ours to free.
            if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
            if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
        }
    }

    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public Point ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
