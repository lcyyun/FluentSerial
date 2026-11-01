using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FluentSerial.Core;
using FluentSerial.Dialogs;

namespace FluentSerial.Features;

public partial class PresetsPage : UserControl, IActivatablePage
{
    public ObservableCollection<PortProfile> VisibleProfiles { get; } = new();

    private bool _initialized;

    public PresetsPage()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => OnActivated();
        // The page is cached by the shell, so a single subscription is enough.
        AppServices.DataChanged += OnDataChanged;
    }

    public void OnActivated()
    {
        if (!_initialized)
        {
            _initialized = true;
            ScopeFilter.SelectedIndex = 0;
        }
        RefreshList();
    }

    private void OnDataChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshList);
            return;
        }
        RefreshList();
    }

    private void RefreshList()
    {
        if (!IsLoaded) return;

        IEnumerable<PortProfile> query = AppServices.Data.Profiles;
        var scope = (ScopeFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        if (scope == "favorites") query = query.Where(p => p.Favorite);

        var search = SearchBox.Text.Trim();
        if (search.Length > 0)
        {
            query = query.Where(p =>
                p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.PortName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.PortLabel.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.Notes.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                p.ParamsLabel.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        var sorted = query
            .OrderByDescending(p => p.Favorite)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();

        VisibleProfiles.Clear();
        foreach (var profile in sorted) VisibleProfiles.Add(profile);
        PresetList.ItemsSource = VisibleProfiles;

        var total = AppServices.Data.Profiles.Count;
        var filtering = search.Length > 0 || scope == "favorites";
        PresetCountText.Text = filtering ? $"{sorted.Count} / {total} 个预设" : $"{total} 个预设";

        var empty = sorted.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        PresetScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty)
        {
            EmptyTitle.Text = total == 0 ? "还没有端口预设" : "没有匹配的预设";
            EmptyHint.Text = total == 0
                ? "保存常用的串口参数，连接时一键打开设备"
                : "试试更换搜索关键词或筛选条件";
            EmptyNewButton.Visibility = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResetFilterButton.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void ScopeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshList();

    private void ResetFilter_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        ScopeFilter.SelectedIndex = 0;
        SearchBox.Focus();
    }

    private void FavoritePreset_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is not { } profile) return;
        profile.Favorite = !profile.Favorite;
        AppServices.NotifyDataChanged();
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e) => ShowPresetDialog(null);

    private void EditPreset_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } profile) ShowPresetDialog(profile);
    }

    private void ConnectPreset_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } profile) AppServices.MainWindow?.ConnectProfile(profile);
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is not { } profile) return;

        var inUse = AppServices.Serial.IsOpen &&
                    string.Equals(AppServices.Serial.Options?.ProfileId, profile.Id, StringComparison.Ordinal);
        var message = inUse
            ? $"预设「{profile.Name}」正在当前会话中使用。删除预设不会断开串口连接，是否继续？"
            : $"删除端口预设「{profile.Name}」？此操作不可撤销。";
        if (!ConfirmDialog.Show(AppServices.MainWindow, "删除端口预设", message, "删除", danger: true)) return;

        AppServices.Data.Profiles.Remove(profile);
        AppServices.Audit.Add(AuditLevel.Warning, "profile", $"删除端口预设 {profile.Name}");
        AppServices.NotifyDataChanged();
        RefreshList();
    }

    private void PresetCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement { DataContext: PortProfile profile })
        {
            e.Handled = true;
            AppServices.MainWindow?.ConnectProfile(profile);
        }
    }

    private void ShowPresetDialog(PortProfile? editing)
    {
        var dialog = new PresetDialog(AppServices.Data.Profiles, editing) { Owner = AppServices.MainWindow };
        if (dialog.ShowDialog() != true) return;
        RefreshList();
    }

    private static PortProfile? ProfileFrom(object sender) => (sender as Button)?.Tag as PortProfile;
}
