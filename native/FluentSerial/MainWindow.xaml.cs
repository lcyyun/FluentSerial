using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluentSerial.Core;
using FluentSerial.Dialogs;
using FluentSerial.Features;
using Wpf.Ui.Controls;

namespace FluentSerial;

public interface IActivatablePage
{
    void OnActivated();
}

public partial class MainWindow : FluentWindow
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private bool _navigating;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AppServices.Serial.StateChanged += OnSerialStateChanged;
        ThemeService.ThemeChanged += UpdateThemeButton;
        Closed += (_, _) =>
        {
            AppServices.Serial.StateChanged -= OnSerialStateChanged;
            ThemeService.ThemeChanged -= UpdateThemeButton;
        };
    }

    public TerminalPage? Terminal => _pages.TryGetValue("terminal", out var page) ? page as TerminalPage : null;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Navigate("terminal");
        UpdateThemeButton();
        UpdateConnectionUi();
        AppServices.Log($"主窗口已加载 {ActualWidth}x{ActualHeight}");
    }

    private UserControl CreatePage(string tag) => tag switch
    {
        "terminal" => new TerminalPage(),
        "presets" => new PresetsPage(),
        "snippets" => new SnippetsPage(),
        "logs" => new LogsPage(),
        "settings" => new SettingsPage(),
        _ => new TerminalPage()
    };

    public void Navigate(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = CreatePage(tag);
            _pages[tag] = page;
        }
        ContentHost.Content = page;
        (page as IActivatablePage)?.OnActivated();
        SetNavSelection(tag);
        UpdateSidebarVisibility(tag);
    }

    private void SetNavSelection(string tag)
    {
        _navigating = true;
        try
        {
            foreach (var list in new[] { NavList, ToolNavList })
            {
                list.SelectedItem = list.Items.OfType<ListBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
            }
        }
        finally
        {
            _navigating = false;
        }
    }

    private void UpdateSidebarVisibility(string tag)
    {
        // Keep the primary action meaningful on every page; the shell always targets the console.
        UpdateConnectionUi();
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating) return;
        if (NavList.SelectedItem is ListBoxItem { Tag: string tag })
        {
            ToolNavList.SelectedItem = null;
            Navigate(tag);
        }
    }

    private void ToolNavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating) return;
        if (ToolNavList.SelectedItem is ListBoxItem { Tag: string tag })
        {
            NavList.SelectedItem = null;
            Navigate(tag);
        }
    }

    public void ConnectProfile(PortProfile profile)
    {
        Navigate("terminal");
        Terminal?.ConnectWithProfile(profile);
    }

    public void SendQuickCommand(QuickCommand command)
    {
        Navigate("terminal");
        Terminal?.SendQuickCommand(command);
    }

    public void OpenSerialConsole()
    {
        Navigate("terminal");
        Terminal?.FocusConnectionArea();
    }

    public void RefreshVisiblePage()
    {
        if (ContentHost.Content is IActivatablePage activatable) activatable.OnActivated();
    }

    public void RefreshThemeUi() => UpdateThemeButton();

    /// <summary>Test hook used by --ui-smoke to verify navigation and page construction.</summary>
    public UserControl? NavigateAndGetPage(string tag)
    {
        Navigate(tag);
        return ContentHost.Content as UserControl;
    }

    /// <summary>Test hook used by --ui-smoke; exercises the same handler as the footer button.</summary>
    public void ToggleThemeForSmoke() => ToggleTheme_Click(this, new RoutedEventArgs());

    private void SidebarConnect_Click(object sender, RoutedEventArgs e)
    {
        Navigate("terminal");
        Terminal?.ToggleConnect();
    }

    private void SidebarStatus_Click(object sender, RoutedEventArgs e) => OpenSerialConsole();

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.Theme = ThemeService.IsDark ? "Light" : "Dark";
        ThemeService.Apply(AppServices.Settings.Theme);
        AppServices.Store.Save();
        UpdateThemeButton();
        RefreshVisiblePage();
    }

    private void UpdateThemeButton()
    {
        ThemeGlyph.Text = ThemeService.IsDark ? "\uE706" : "\uE708";
        ThemeButton.ToolTip = ThemeService.IsDark ? "切换到浅色模式" : "切换到深色模式";
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Navigate("settings");

    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ShortcutsDialog { Owner = this };
        dialog.ShowDialog();
    }

    private void OnSerialStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateConnectionUi);
            return;
        }
        UpdateConnectionUi();
    }

    private void UpdateConnectionUi()
    {
        var serial = AppServices.Serial;
        var state = serial.State;
        switch (state)
        {
            case SerialLinkState.Open:
                SidebarConnectLabel.Text = "关闭串口";
                SidebarConnectGlyph.Text = "\uE769";
                SidebarStatusText.Text = $"{serial.PortLabel}  {serial.ParamsLabel}";
                SidebarStatusDot.Fill = (Brush)FindResource("AppSuccessBrush");
                break;
            case SerialLinkState.Opening:
                SidebarConnectLabel.Text = "正在打开";
                SidebarConnectGlyph.Text = "\uE768";
                SidebarStatusText.Text = "正在打开串口";
                SidebarStatusDot.Fill = (Brush)FindResource("AppWarningBrush");
                break;
            case SerialLinkState.Error:
                SidebarConnectLabel.Text = "打开串口";
                SidebarConnectGlyph.Text = "\uE768";
                SidebarStatusText.Text = string.IsNullOrWhiteSpace(serial.LastError) ? "串口异常" : serial.LastError;
                SidebarStatusDot.Fill = (Brush)FindResource("AppDangerBrush");
                break;
            default:
                SidebarConnectLabel.Text = "打开串口";
                SidebarConnectGlyph.Text = "\uE768";
                SidebarStatusText.Text = "未连接串口";
                SidebarStatusDot.Fill = (Brush)FindResource("AppTextTertiaryBrush");
                break;
        }
        SidebarConnectButton.IsEnabled = state != SerialLinkState.Opening;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D1:
                    Navigate("terminal");
                    e.Handled = true;
                    return;
                case Key.D2:
                    Navigate("presets");
                    e.Handled = true;
                    return;
                case Key.D3:
                    Navigate("snippets");
                    e.Handled = true;
                    return;
                case Key.D4:
                    Navigate("logs");
                    e.Handled = true;
                    return;
                case Key.OemComma:
                    Navigate("settings");
                    e.Handled = true;
                    return;
                case Key.O:
                    Navigate("terminal");
                    Terminal?.ToggleConnect();
                    e.Handled = true;
                    return;
                case Key.L:
                    Navigate("terminal");
                    Terminal?.ClearConsoleFromShortcut();
                    e.Handled = true;
                    return;
                case Key.S:
                    Navigate("terminal");
                    Terminal?.SaveConsoleFromShortcut();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.F5 && ContentHost.Content is TerminalPage)
        {
            Terminal?.RefreshPorts();
            e.Handled = true;
        }
    }
}
