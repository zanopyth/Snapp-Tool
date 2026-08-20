using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SnapTool.Capture;

/// <summary>
/// Detects the top-level window and, where possible, the specific native child control
/// under a screen point, so the region selector can snap/highlight to real UI boundaries.
/// </summary>
internal static class WindowDetector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr hwndParent, POINT pt, uint uFlags);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint CWP_SKIPINVISIBLE = 0x1;
    private const uint CWP_SKIPDISABLED = 0x2;
    private const uint CWP_SKIPTRANSPARENT = 0x4;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private static Rectangle ToRect(RECT r) => Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);

    // On Windows 10/11, GetWindowRect on a top-level window includes an invisible resize-border/shadow
    // margin (a few px per side) that isn't actually part of the visible window, so highlighting it
    // leaves a gap all around. DWMWA_EXTENDED_FRAME_BOUNDS reports the true visible frame instead.
    private static Rectangle GetVisibleWindowRect(IntPtr hWnd, RECT fallback)
    {
        if (DwmGetWindowAttributeRect(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var extended, Marshal.SizeOf<RECT>()) == 0)
            return ToRect(extended);
        return ToRect(fallback);
    }

    /// <summary>All visible top-level window rects, front-to-back (EnumWindows order == Z-order).</summary>
    public static List<(IntPtr Handle, Rectangle Bounds)> GetTopLevelWindows()
    {
        var result = new List<(IntPtr, Rectangle)>();

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (!GetWindowRect(hWnd, out var rect)) return true;

            var bounds = GetVisibleWindowRect(hWnd, rect);
            if (bounds.Width <= 0 || bounds.Height <= 0) return true;

            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            result.Add((hWnd, bounds));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Finds the frontmost top-level window containing the given screen point.</summary>
    public static (IntPtr Handle, Rectangle Bounds)? HitTest(List<(IntPtr Handle, Rectangle Bounds)> windows, Point screenPoint)
    {
        foreach (var w in windows)
        {
            if (w.Bounds.Contains(screenPoint)) return w;
        }
        return null;
    }

    /// <summary>
    /// Walks from a top-level window down through its native child controls at the given point,
    /// returning the chain of bounding rects from outermost (window) to innermost (control).
    /// Falls back to just the window rect for apps that don't expose native child HWNDs.
    /// </summary>
    public static List<Rectangle> GetHoverChain(IntPtr topLevel, Rectangle topLevelBounds, Point screenPoint)
    {
        var chain = new List<Rectangle> { topLevelBounds };

        var current = topLevel;
        var guard = 0;
        while (guard++ < 32)
        {
            var clientPt = new POINT { X = screenPoint.X, Y = screenPoint.Y };
            if (!ScreenToClient(current, ref clientPt)) break;

            var child = ChildWindowFromPointEx(current, clientPt, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
            if (child == IntPtr.Zero || child == current) break;

            if (!GetWindowRect(child, out var rect)) break;
            var bounds = ToRect(rect);
            if (bounds.Width <= 0 || bounds.Height <= 0) break;

            if (bounds == chain[^1]) break; // no finer detail available
            chain.Add(bounds);
            current = child;
        }

        return chain;
    }
}
