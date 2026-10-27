using System.IO;
using System.Windows;

namespace FluentSerial.Core;

/// <summary>
/// Process-wide native services. Initialized once from App.OnStartup.
/// </summary>
public static class AppServices
{
    public static AppPaths Paths { get; } = new();
    public static DataStore Store { get; } = new(Paths);
    public static AuditService Audit { get; } = new();
    public static SerialSessionService Serial { get; } = new();

    public static AppData Data => Store.Data;
    public static AppSettings Settings => Store.Data.Settings;
    public static MainWindow? MainWindow { get; set; }

    /// <summary>Raised after a page mutates persisted data outside of the serial session.</summary>
    public static event Action? DataChanged;

    public static void Initialize()
    {
        Paths.Ensure();
        Store.Load();
        Audit.Initialize(Store);
        Log($"FluentSerial 启动，数据目录：{Paths.Root}");
    }

    public static void Shutdown()
    {
        try
        {
            Serial.Close("应用退出");
            Store.Save();
        }
        catch (Exception ex)
        {
            LogError("退出时保存数据失败", ex);
        }
    }

    public static void NotifyDataChanged()
    {
        Store.Save();
        DataChanged?.Invoke();
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            File.AppendAllText(Path.Combine(Paths.Root, "app.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never crash the app
        }
    }

    public static void LogError(string message, Exception? ex = null)
    {
        Log($"ERROR {message}{(ex is null ? string.Empty : $": {ex.GetType().Name}: {ex.Message}")}");
        if (ex is not null)
            Log(ex.StackTrace ?? string.Empty);
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LogError("打开资源管理器失败", ex);
        }
    }

    public static void ShowError(string message, string title = "FluentSerial")
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
