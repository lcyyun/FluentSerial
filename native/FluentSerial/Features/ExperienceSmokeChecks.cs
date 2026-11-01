using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FluentSerial.Core;

namespace FluentSerial.Features;

/// <summary>Exercises user-visible regressions only when called by --ui-smoke.</summary>
public static class ExperienceSmokeChecks
{
    public static IReadOnlyList<(string Name, bool Ok, string Detail)> Check(MainWindow window)
    {
        var checks = new List<(string Name, bool Ok, string Detail)>();
        void Verify(string name, bool ok, string detail = "") => checks.Add((name, ok, detail));
        void Render()
        {
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        }
        T Page<T>(string name) where T : UserControl
        {
            var page = (T)window.NavigateAndGetPage(name)!;
            Render();
            return page;
        }
        static T Control<T>(FrameworkElement page, string name) where T : FrameworkElement
            => (T)page.FindName(name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var settings = Page<SettingsPage>("settings");
        var font = Control<TextBox>(settings, "FontSizeBox");
        var originalFont = font.Text;
        var originalValue = AppServices.Settings.ConsoleFontSize;
        var savedStore = File.ReadAllText(AppServices.Paths.StoreFile);
        font.Text = "NaN";
        settings.SaveSettings();
        Verify("设置拒绝 NaN 且不修改存储", settings.SaveStatusForSmoke.Contains("9-24") &&
            AppServices.Settings.ConsoleFontSize == originalValue &&
            File.ReadAllText(AppServices.Paths.StoreFile) == savedStore, settings.SaveStatusForSmoke);

        var draft = originalFont == "14.5" ? "15.5" : "14.5";
        font.Text = draft;
        Page<TerminalPage>("terminal");
        settings = Page<SettingsPage>("settings");
        Verify("设置草稿跨导航保留", font.Text == draft &&
            settings.SaveStatusForSmoke.Contains("未保存"), font.Text);
        Click(Control<Button>(settings, "DiscardButton"));
        Verify("撤销设置恢复已保存值", font.Text == originalFont &&
            AppServices.Settings.ConsoleFontSize == originalValue &&
            !Control<Button>(settings, "DiscardButton").IsEnabled, font.Text);

        var logs = Page<LogsPage>("logs");
        logs.ShowSessionTab();
        var sessions = Control<ItemsControl>(logs, "SessionList");
        var sessionCount = sessions.Items.Count;
        var search = Control<TextBox>(logs, "SearchBox");
        search.Text = "no-session-" + Guid.NewGuid().ToString("N");
        Verify("日志搜索显示空结果", sessions.Items.Count == 0 &&
            Control<TextBlock>(logs, "EmptySessionsText").Visibility == Visibility.Visible);
        Click(Control<Button>(logs, "ResetSearchButton"));
        Verify("清除日志搜索恢复会话", sessionCount > 0 && sessions.Items.Count == sessionCount && search.Text.Length == 0,
            $"{sessions.Items.Count}/{sessionCount}");

        var command = new QuickCommand { Name = "000 UI smoke 收藏", Payload = "AT", Group = "all" };
        try
        {
            AppServices.Data.Commands.Add(command);
            AppServices.NotifyDataChanged();
            var snippets = Page<SnippetsPage>("snippets");
            var groups = Control<ComboBox>(snippets, "GroupFilter");
            groups.SelectedItem = groups.Items.OfType<ComboBoxItem>().First(item => Equals(item.Content, "all"));
            Verify("all 分组可独立筛选", snippets.VisibleCommands.Contains(command) &&
                snippets.VisibleCommands.All(item => item.Group == "all"));
            Button Favorite() => Descendants(snippets).OfType<Button>().First(button =>
                ReferenceEquals(button.Tag, command) && AutomationProperties.GetName(button) == "收藏或取消收藏指令");
            Render();
            Click(Favorite());
            groups.SelectedIndex = 1;
            Render();
            Verify("收藏后出现在收藏筛选", command.Favorite && snippets.VisibleCommands.Contains(command));
            Click(Favorite());
            Verify("取消收藏即时移出筛选", !command.Favorite && !snippets.VisibleCommands.Contains(command));
            Click(Control<Button>(snippets, "ResetFilterButton"));
        }
        finally
        {
            AppServices.Data.Commands.Remove(command);
            AppServices.NotifyDataChanged();
            Page<TerminalPage>("terminal");
        }
        return checks;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
