using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTool.Capture;

/// <summary>Hidden message-only-ish window used to receive WM_HOTKEY for global hotkeys.</summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    public event Action<int>? HotkeyPressed;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool[] _registeredIds = new bool[16];

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    public bool Register(int id, uint modifiers, uint vk)
    {
        bool ok = RegisterHotKey(Handle, id, modifiers, vk);
        if (ok && id >= 0 && id < _registeredIds.Length) _registeredIds[id] = true;
        return ok;
    }

    public void Unregister(int id)
    {
        if (id < 0 || id >= _registeredIds.Length || !_registeredIds[id]) return;
        UnregisterHotKey(Handle, id);
        _registeredIds[id] = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            HotkeyPressed?.Invoke(m.WParam.ToInt32());
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        for (int id = 0; id < _registeredIds.Length; id++)
        {
            if (_registeredIds[id]) UnregisterHotKey(Handle, id);
        }
        DestroyHandle();
    }
}
