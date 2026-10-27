using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FluentSerial.Core;
using FluentSerial.Dialogs;
using Microsoft.Win32;

namespace FluentSerial.Features;

public partial class SnippetsPage : UserControl, IActivatablePage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ObservableCollection<QuickCommand> VisibleCommands { get; } = new();

    public SnippetsPage()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => OnActivated();
        AppServices.DataChanged += OnDataChanged;
    }

    public void OnActivated()
    {
        RebuildGroupFilter();
        RefreshList();
    }

    private void OnDataChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnDataChanged);
            return;
        }
        RebuildGroupFilter();
        RefreshList();
    }

    private void RebuildGroupFilter()
    {
        var previous = (GroupFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        GroupFilter.Items.Clear();
        GroupFilter.Items.Add(new ComboBoxItem { Content = "全部指令", Tag = "all" });
        GroupFilter.Items.Add(new ComboBoxItem { Content = "仅收藏", Tag = "favorites" });
        foreach (var group in AppServices.Data.Commands
                     .Select(c => c.Group)
                     .Where(g => !string.IsNullOrWhiteSpace(g))
                     .Distinct(StringComparer.CurrentCulture)
                     .OrderBy(g => g, StringComparer.CurrentCulture))
        {
            GroupFilter.Items.Add(new ComboBoxItem { Content = group, Tag = group });
        }

        var match = GroupFilter.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, previous, StringComparison.Ordinal));
        GroupFilter.SelectedItem = match ?? GroupFilter.Items[0];
    }

    private void RefreshList()
    {
        if (!IsLoaded) return;

        IEnumerable<QuickCommand> query = AppServices.Data.Commands;
        var scope = (GroupFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        if (scope == "favorites") query = query.Where(c => c.Favorite);
        else if (!string.IsNullOrEmpty(scope) && scope != "all") query = query.Where(c => c.Group == scope);

        var search = SearchBox.Text.Trim();
        if (search.Length > 0)
        {
            query = query.Where(c =>
                c.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Payload.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Group.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.Description.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                c.ModeLabel.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var sorted = query
            .OrderByDescending(c => c.Favorite)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();

        VisibleCommands.Clear();
        foreach (var command in sorted) VisibleCommands.Add(command);
        CommandList.ItemsSource = VisibleCommands;

        var total = AppServices.Data.Commands.Count;
        var filtering = search.Length > 0 || (scope is not null && scope != "all");
        CommandCountText.Text = filtering ? $"{sorted.Count} / {total} 条指令" : $"{total} 条指令";

        var empty = sorted.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            EmptyTitle.Text = total == 0 ? "还没有快捷指令" : "没有匹配的指令";
            EmptyHint.Text = total == 0
                ? "把常用指令保存下来，一键发送到串口"
                : "试试更换搜索关键词或筛选条件";
            EmptyNewButton.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void GroupFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshList();

    private void NewCommand_Click(object sender, RoutedEventArgs e) => ShowCommandDialog(null);

    private void SendCommand_Click(object sender, RoutedEventArgs e) => SendCommand(CommandFrom(sender));

    private void EditCommand_Click(object sender, RoutedEventArgs e)
    {
        if (CommandFrom(sender) is { } command) ShowCommandDialog(command);
    }

    private void DeleteCommand_Click(object sender, RoutedEventArgs e)
    {
        if (CommandFrom(sender) is not { } command) return;
        if (!ConfirmDialog.Show(AppServices.MainWindow, "删除快捷指令",
                $"删除快捷指令「{command.Name}」？此操作不可撤销。", "删除", danger: true)) return;

        AppServices.Data.Commands.Remove(command);
        AppServices.Audit.Add(AuditLevel.Warning, "system", $"删除快捷指令 {command.Name}");
        AppServices.NotifyDataChanged();
        RefreshList();
    }

    private void SnippetRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement { DataContext: QuickCommand command })
        {
            e.Handled = true;
            SendCommand(command);
        }
    }

    private void SendCommand(QuickCommand? command)
    {
        if (command is null) return;
        AppServices.MainWindow?.SendQuickCommand(command);
    }

    private void ShowCommandDialog(QuickCommand? editing)
    {
        var dialog = new SnippetDialog(editing) { Owner = AppServices.MainWindow };
        if (dialog.ShowDialog() != true) return;
        RefreshList();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "导入快捷指令",
            Filter = "JSON 文件|*.json|所有文件|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog() != true) return;

        List<QuickCommand> imported;
        try
        {
            var json = File.ReadAllText(picker.FileName, Encoding.UTF8);
            imported = JsonSerializer.Deserialize<List<QuickCommand>>(json, JsonOptions) ?? new List<QuickCommand>();
        }
        catch (JsonException ex)
        {
            AppServices.LogError("导入快捷指令失败：JSON 格式无效", ex);
            MessageBox.Show($"文件不是有效的快捷指令 JSON。\n{ex.Message}", "导入快捷指令",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        catch (Exception ex)
        {
            AppServices.LogError("读取快捷指令文件失败", ex);
            MessageBox.Show($"无法读取文件：{ex.Message}", "导入快捷指令",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Filtering with OfType also drops JSON "null" entries so a bad element cannot
        // leave a half-finished import behind.
        var parsed = imported.OfType<QuickCommand>().ToList();
        if (parsed.Count == 0)
        {
            MessageBox.Show("该文件中没有可导入的指令。", "导入快捷指令",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var command in parsed)
        {
            command.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(command.Name)) command.Name = "未命名指令";
            if (string.IsNullOrWhiteSpace(command.Group)) command.Group = "自定义";
            if (command.Payload is null) command.Payload = string.Empty;
            if (command.Description is null) command.Description = string.Empty;
            AppServices.Data.Commands.Add(command);
        }

        AppServices.Audit.Add(AuditLevel.Info, "system", $"导入 {parsed.Count} 条快捷指令",
            Path.GetFileName(picker.FileName));
        AppServices.NotifyDataChanged();
        RebuildGroupFilter();
        RefreshList();
        MessageBox.Show($"已导入 {parsed.Count} 条快捷指令。", "导入完成",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (AppServices.Data.Commands.Count == 0)
        {
            MessageBox.Show("还没有可导出的快捷指令。", "导出快捷指令",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new SaveFileDialog
        {
            Title = "导出快捷指令",
            Filter = "JSON 文件|*.json",
            DefaultExt = ".json",
            FileName = $"fluentserial-commands-{DateTime.Now:yyyyMMdd}.json"
        };
        if (picker.ShowDialog() != true) return;

        try
        {
            var json = JsonSerializer.Serialize(AppServices.Data.Commands.ToList(), JsonOptions);
            File.WriteAllText(picker.FileName, json, new UTF8Encoding(false));
            AppServices.Audit.Add(AuditLevel.Info, "system",
                $"导出 {AppServices.Data.Commands.Count} 条快捷指令", picker.FileName);
            MessageBox.Show($"已导出 {AppServices.Data.Commands.Count} 条快捷指令。", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppServices.LogError("导出快捷指令失败", ex);
            MessageBox.Show($"导出失败：{ex.Message}", "导出快捷指令",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static QuickCommand? CommandFrom(object sender) => (sender as Button)?.Tag as QuickCommand;
}
