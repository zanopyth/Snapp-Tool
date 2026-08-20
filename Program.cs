using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SnapTool.Forms;

namespace SnapTool;

internal static class Program
{
    private const string SingleInstanceMutexName = "SnapTool_SingleInstance_9F3E2C1A";
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [STAThread]
    private static void Main()
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Another SnapTool is already running — surface it instead of starting a second one.
            var existing = FindWindow(null, "SnapTool");
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, SW_RESTORE);
                SetForegroundWindow(existing);
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
