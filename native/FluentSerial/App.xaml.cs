using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FluentSerial.Core;

namespace FluentSerial;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private string? _capturePath;
    private string? _capturePage;
    private string? _theme;
    private string? _dialog;
    private string? _tab;
    private string? _uiSmokeOut;
    private bool _demo;
    private bool _seedDemo;
    private bool _rxHex;
    private bool _uiSmoke;
    private int _captureDelayMs = 700;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        ParseArgs();

        try
        {
            AppServices.Initialize();
            if (_seedDemo) DemoDataSeeder.Seed();
            if (_rxHex)
            {
                AppServices.Settings.ReceiveMode = PayloadMode.Hex;
                AppServices.Store.Save();
            }
            if (!string.IsNullOrWhiteSpace(_theme))
            {
                AppServices.Settings.Theme = _theme;
                AppServices.Store.Save();
            }
            ThemeService.Apply(AppServices.Settings.Theme);

            var window = new MainWindow();
            AppServices.MainWindow = window;
            MainWindow = window;
            window.Show();

            if (_capturePath is not null)
                window.ContentRendered += OnCaptureReady;
            if (_uiSmoke)
                window.ContentRendered += OnUiSmokeReady;
        }
        catch (Exception ex)
        {
            AppServices.LogError("启动失败", ex);
            MessageBox.Show($"FluentSerial 启动失败：\n{ex.Message}", "FluentSerial", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ParseArgs()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--capture" when i + 1 < args.Length:
                    _capturePath = Path.GetFullPath(args[++i]);
                    break;
                case "--page" when i + 1 < args.Length:
                    _capturePage = args[++i];
                    break;
                case "--theme" when i + 1 < args.Length:
                    _theme = args[++i];
                    break;
                case "--dialog" when i + 1 < args.Length:
                    _dialog = args[++i];
                    break;
                case "--tab" when i + 1 < args.Length:
                    _tab = args[++i];
                    break;
                case "--demo":
                    _demo = true;
                    break;
                case "--seed-demo":
                    _seedDemo = true;
                    break;
                case "--rx-hex":
                    _rxHex = true;
                    break;
                case "--ui-smoke":
                    _uiSmoke = true;
                    break;
                case "--ui-smoke-out" when i + 1 < args.Length:
                    _uiSmokeOut = Path.GetFullPath(args[++i]);
                    break;
                case "--delay" when i + 1 < args.Length && int.TryParse(args[++i], out var delay):
                    _captureDelayMs = Math.Clamp(delay, 100, 15000);
                    break;
            }
        }
    }

    private async void OnCaptureReady(object? sender, EventArgs e)
    {
        if (MainWindow is not MainWindow window || _capturePath is null) return;
        window.ContentRendered -= OnCaptureReady;

        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Task.Delay(250);

            if (!string.IsNullOrWhiteSpace(_capturePage))
                window.Navigate(_capturePage);

            if (!string.IsNullOrWhiteSpace(_tab) && window.ContentHost.Content is not null)
            {
                var method = window.ContentHost.Content.GetType()
                    .GetMethod(_tab == "audit" ? "ShowAuditTab" : "ShowSessionTab");
                method?.Invoke(window.ContentHost.Content, null);
            }

            if (_demo)
            {
                if (!string.Equals(_capturePage, "terminal", StringComparison.OrdinalIgnoreCase))
                    window.Navigate("terminal");
                if (window.Terminal is not null)
                    await window.Terminal.OpenDemoForCaptureAsync();
            }

            if (!string.IsNullOrWhiteSpace(_dialog))
            {
                CaptureDialog(window);
                return;
            }

            await Task.Delay(_captureDelayMs);
            window.UpdateLayout();
            MoveCursorAwayFromWindow();
            NativeScreenshot.Capture(window, _capturePath);
        }
        catch (Exception ex)
        {
            AppServices.LogError("截图失败", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    private void CaptureDialog(MainWindow window)
    {
        Window? dialog = _dialog switch
        {
            "shortcuts" => new Dialogs.ShortcutsDialog { Owner = window },
            "preset" => new Dialogs.PresetDialog(AppServices.Data.Profiles,
                AppServices.Data.Profiles.FirstOrDefault(p => p.PortName == "DEMO")
                ?? AppServices.Data.Profiles.FirstOrDefault(p => p.Favorite))
            {
                Owner = window
            },
            "preset-new" => new Dialogs.PresetDialog(AppServices.Data.Profiles, null) { Owner = window },
            "snippet" => new Dialogs.SnippetDialog(AppServices.Data.Commands.FirstOrDefault(c => c.Name == "读取传感器")
                                                    ?? AppServices.Data.Commands.FirstOrDefault())
            {
                Owner = window
            },
            "snippet-new" => new Dialogs.SnippetDialog(null) { Owner = window },
            _ => null
        };
        if (dialog is null)
        {
            AppServices.Log("未知对话框参数: " + _dialog);
            return;
        }
        dialog.Loaded += async (_, _) =>
        {
            await Task.Delay(_captureDelayMs);
            MoveCursorAwayFromWindow();
            NativeScreenshot.Capture(dialog, _capturePath!);
            dialog.Close();
        };
        dialog.ShowDialog();
    }

    /// <summary>The captured window must not show a hovered card because of the physical cursor.</summary>
    private static void MoveCursorAwayFromWindow()
    {
        try
        {
            SetCursorPos(0, 0);
        }
        catch
        {
            // cursor position is cosmetic; capture still succeeds
        }
    }

    /// <summary>
    /// --ui-smoke drives real navigation, a DEMO connection, a UI send, a display-mode toggle
    /// and a theme switch through the same handlers the user clicks. Exits non-zero on failure.
    /// </summary>
    private async void OnUiSmokeReady(object? sender, EventArgs e)
    {
        if (MainWindow is not MainWindow window) return;
        window.ContentRendered -= OnUiSmokeReady;

        var checks = new List<(string Name, bool Ok, string Detail)>();
        void Check(string name, bool ok, string detail = "") => checks.Add((name, ok, detail));

        try
        {
            await Task.Delay(400);
            foreach (var (tag, expected) in new[]
                     {
                         ("presets", "PresetsPage"),
                         ("snippets", "SnippetsPage"),
                         ("logs", "LogsPage"),
                         ("settings", "SettingsPage"),
                         ("terminal", "TerminalPage")
                     })
            {
                var page = window.NavigateAndGetPage(tag);
                await Task.Delay(180);
                var actual = page?.GetType().Name ?? "(null)";
                Check($"导航 {tag}", actual == expected, actual);
            }

            window.Navigate("terminal");
            var terminal = window.Terminal;
            if (terminal is null)
            {
                Check("终端页可用", false, "TerminalPage 未创建");
            }
            else
            {
                var rxBefore = AppServices.Serial.BytesRx;
                var opened = await terminal.OpenDemoForSmokeAsync();
                Check("打开 DEMO 串口", opened && AppServices.Serial.IsOpen,
                    $"{AppServices.Serial.PortLabel}  {AppServices.Serial.State}");

                await Task.Delay(700);
                Check("接收演示数据", AppServices.Serial.BytesRx > rxBefore,
                    $"RX {AppServices.Serial.BytesRx} B");

                var txBefore = AppServices.Serial.BytesTx;
                var sent = terminal.SendForSmoke("AT+VER?");
                await Task.Delay(400);
                Check("UI 发送数据", sent && AppServices.Serial.BytesTx > txBefore,
                    $"TX {AppServices.Serial.BytesTx} B");
                Check("控制台渲染", terminal.ConsoleLineCount > 0,
                    $"{terminal.ConsoleLineCount} lines");

                terminal.ToggleHexForSmoke();
                await Task.Delay(300);
                Check("切换 HEX 视图", terminal.ConsoleLineCount > 0,
                    $"{terminal.ConsoleLineCount} lines");

                var themeBefore = ThemeService.IsDark;
                window.ToggleThemeForSmoke();
                await Task.Delay(350);
                Check("切换深浅色主题", ThemeService.IsDark != themeBefore,
                    ThemeService.IsDark ? "Dark" : "Light");

                var settingsPage = window.NavigateAndGetPage("settings") as Features.SettingsPage;
                settingsPage?.SaveForSmoke();
                await Task.Delay(250);
                Check("设置保存", settingsPage is not null && settingsPage.SaveStatusForSmoke.Contains("已保存", StringComparison.Ordinal),
                    settingsPage?.SaveStatusForSmoke ?? "SettingsPage 未创建");

                terminal.CloseForSmoke();
                await Task.Delay(300);
                Check("关闭串口", !AppServices.Serial.IsOpen, AppServices.Serial.State.ToString());

                window.Navigate("terminal");
                foreach (var check in terminal.CheckRenderingForSmoke()) Check(check.Name, check.Ok, check.Detail);
                foreach (var check in terminal.CheckSendExperienceForSmoke()) Check(check.Name, check.Ok, check.Detail);
                foreach (var check in terminal.CheckLayoutForSmoke(window)) Check(check.Name, check.Ok, check.Detail);
                foreach (var check in Features.ExperienceSmokeChecks.Check(window)) Check(check.Name, check.Ok, check.Detail);

                Check("会话记录落盘",
                    AppServices.Data.Sessions.Any(s => s.EndedAt is not null && File.Exists(s.FilePath)),
                    $"{AppServices.Data.Sessions.Count} sessions");
                Check("审计事件", AppServices.Data.Audit.Count > 0, $"{AppServices.Data.Audit.Count} events");
                Check("本地存储", File.Exists(AppServices.Paths.StoreFile), AppServices.Paths.StoreFile);
            }
        }
        catch (Exception ex)
        {
            AppServices.LogError("UI 冒烟测试异常", ex);
            Check("未捕获异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            var passed = checks.Count(c => c.Ok);
            var failed = checks.Count - passed;
            var report = new System.Text.StringBuilder();
            report.AppendLine("FluentSerial UI smoke");
            report.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            report.AppendLine($"Data: {AppServices.Paths.Root}");
            foreach (var check in checks)
                report.AppendLine($"[{(check.Ok ? "PASS" : "FAIL")}] {check.Name}{(string.IsNullOrWhiteSpace(check.Detail) ? string.Empty : "  -  " + check.Detail)}");
            report.AppendLine($"RESULT passed={passed} failed={failed}");
            try
            {
                var path = _uiSmokeOut ?? Path.Combine(AppServices.Paths.Root, "ui-smoke.txt");
                File.WriteAllText(path, report.ToString(), new System.Text.UTF8Encoding(false));
                Console.WriteLine(report.ToString());
            }
            catch (Exception ex)
            {
                AppServices.LogError("写入 UI 冒烟报告失败", ex);
            }
            AppServices.Store.Save();
            Shutdown(failed == 0 ? 0 : 1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppServices.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppServices.LogError("未处理的界面异常", e.Exception);
        MessageBox.Show($"发生未处理的错误：\n{e.Exception.Message}", "FluentSerial", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
