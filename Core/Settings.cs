using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;
using SnapTool.Capture;

namespace SnapTool.Core;

internal sealed class AppSettings
{
    public string ScreenshotsFolder { get; set; } = DefaultScreenshotsFolder();
    public bool AutoSaveAfterCapture { get; set; } = true;
    public bool AutoCopyToClipboard { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public bool HideWindowWhileCapturing { get; set; } = true;

    public uint RegionModifiers { get; set; } = HotkeyUtil.MOD_CONTROL | HotkeyUtil.MOD_SHIFT;
    public int RegionKey { get; set; } = (int)Keys.S;
    public uint FullscreenModifiers { get; set; } = HotkeyUtil.MOD_CONTROL | HotkeyUtil.MOD_SHIFT;
    public int FullscreenKey { get; set; } = (int)Keys.F;
    public uint GifModifiers { get; set; } = HotkeyUtil.MOD_CONTROL | HotkeyUtil.MOD_SHIFT;
    public int GifKey { get; set; } = (int)Keys.G;
    public int GifFps { get; set; } = 8;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ToolbarEdge DefaultToolbarPosition { get; set; } = ToolbarEdge.Top;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AfterCaptureAction AfterCaptureAction { get; set; } = AfterCaptureAction.OpenEditor;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnapTool", "settings.json");

    private static string DefaultScreenshotsFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "SnapTool");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // fall through to defaults if the settings file is missing or corrupt
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
