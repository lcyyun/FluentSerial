using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FluentSerial.Core;
using FluentSerial.Dialogs;

namespace FluentSerial.Features;

public partial class LogsPage : UserControl, IActivatablePage
{
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public LogsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _statsTimer.Start();
            OnActivated();
        };
        Unloaded += (_, _) => _statsTimer.Stop();
        _statsTimer.Tick += (_, _) =>
        {
            if (IsLoaded && IsVisible) RefreshStats();
        };
        AppServices.DataChanged += OnDataChanged;
        AppServices.Audit.Changed += OnAuditChanged;
    }

    public void OnActivated()
    {
        if (SectionTabs.SelectedIndex < 0) SectionTabs.SelectedIndex = 0;
        RefreshAll();
    }

    /// <summary>Switches to the audit tab. Used by the screenshot and demo tooling.</summary>
    public void ShowAuditTab()
    {
        SectionTabs.SelectedIndex = 1;
        if (IsLoaded) RefreshAll();
    }

    /// <summary>Switches back to the session tab (the default view).</summary>
    public void ShowSessionTab()
    {
        SectionTabs.SelectedIndex = 0;
        if (IsLoaded) RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshSessions();
        RefreshAudit();
        RefreshStats();
        UpdateCountText();
    }

    private void RefreshSessions()
    {
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        var sessions = AppServices.Data.Sessions.Where(session => search.Length == 0 ||
            Matches(search, session.DisplayPort, session.ParamsLabel, session.FileName,
                session.Note, session.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();
        SessionList.ItemsSource = sessions;
        EmptySessionsText.Text = search.Length == 0 ? "还没有会话记录" : "没有匹配的会话，请更换关键词或清除搜索";
        EmptySessionsText.Visibility = sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshAudit()
    {
        var search = SearchBox?.Text.Trim() ?? string.Empty;
        var events = AppServices.Data.Audit.Where(item => search.Length == 0 ||
            Matches(search, item.Message, item.Detail, item.LevelLabel, item.CategoryLabel,
                item.At.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();
        AuditList.ItemsSource = events;
        EmptyAuditText.Text = search.Length == 0 ? "还没有审计事件" : "没有匹配的事件，请更换关键词或清除搜索";
        EmptyAuditText.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshStats()
    {
        long rx = 0;
        long tx = 0;
        var today = 0;
        var todayDate = DateTime.Today;
        foreach (var session in AppServices.Data.Sessions)
        {
            rx += session.BytesRx;
            tx += session.BytesTx;
            if (session.StartedAt.Date == todayDate) today++;
        }

        SessionTotalText.Text = AppServices.Data.Sessions.Count.ToString();
        RxTotalText.Text = FormatText.Bytes(rx);
        TxTotalText.Text = FormatText.Bytes(tx);
        TodayTotalText.Text = today.ToString();
    }

    private void UpdateCountText()
    {
        var audit = SectionTabs.SelectedIndex == 1;
        var total = audit ? AppServices.Data.Audit.Count : AppServices.Data.Sessions.Count;
        var visible = audit ? AuditList.Items.Count : SessionList.Items.Count;
        var unit = audit ? "条事件" : "条会话";
        var filtering = !string.IsNullOrWhiteSpace(SearchBox?.Text);
        CountText.Text = filtering ? $"{visible} / {total} {unit}" : $"{total} {unit}";
        if (ResetSearchButton is not null)
            ResetSearchButton.Visibility = filtering ? Visibility.Visible : Visibility.Collapsed;
        if (SearchHint is not null)
            SearchHint.Visibility = string.IsNullOrEmpty(SearchBox?.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(string query, params string?[] values)
        => values.Any(value => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SessionList is null || AuditList is null) return;
        RefreshSessions();
        RefreshAudit();
        UpdateCountText();
    }

    private void ResetSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void OnDataChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshAll);
            return;
        }
        RefreshAll();
    }

    private void OnAuditChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshAll);
            return;
        }
        RefreshAll();
    }

    private void SectionTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionsView is null || AuditView is null) return;
        var audit = SectionTabs.SelectedIndex == 1;
        SessionsView.Visibility = audit ? Visibility.Collapsed : Visibility.Visible;
        AuditView.Visibility = audit ? Visibility.Visible : Visibility.Collapsed;
        if (audit) RefreshAudit();
        else RefreshSessions();
        UpdateCountText();
    }

    private void OpenRecordsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppServices.Paths.LogsDirectory);
            AppServices.OpenInExplorer(AppServices.Paths.LogsDirectory);
        }
        catch (Exception ex)
        {
            AppServices.LogError("打开记录目录失败", ex);
            AppServices.ShowError($"无法打开记录目录：{ex.Message}");
        }
    }

    private void OpenSessionFile_Click(object sender, RoutedEventArgs e)
    {
        if (SessionFrom(sender) is not { } session) return;
        if (string.IsNullOrWhiteSpace(session.FilePath))
        {
            AppServices.ShowError("该会话没有可用的记录文件。");
            return;
        }
        try
        {
            if (!File.Exists(session.FilePath))
            {
                AppServices.ShowError("记录文件已被移动或删除。");
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = session.FilePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppServices.LogError("打开会话记录文件失败", ex);
            AppServices.ShowError($"无法打开记录文件：{ex.Message}");
        }
    }

    private void LocateSessionFile_Click(object sender, RoutedEventArgs e)
    {
        if (SessionFrom(sender) is not { } session) return;
        if (string.IsNullOrWhiteSpace(session.FilePath))
        {
            AppServices.ShowError("该会话没有可用的记录文件。");
            return;
        }
        if (!File.Exists(session.FilePath))
        {
            AppServices.ShowError("记录文件已被移动或删除。");
            return;
        }
        AppServices.OpenInExplorer(session.FilePath);
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionFrom(sender) is not { } session) return;
        if (session.EndedAt is null && AppServices.Serial.IsOpen &&
            session.StartedAt == AppServices.Serial.ConnectedAt)
        {
            AppServices.ShowError("此会话仍在进行，请先关闭串口后再删除记录。");
            return;
        }
        if (!ConfirmDialog.Show(AppServices.MainWindow, "删除会话记录",
                $"删除会话记录「{session.DisplayPort}」及其记录文件？此操作不可撤销。", "删除", danger: true)) return;

        try
        {
            if (!string.IsNullOrWhiteSpace(session.FilePath) && File.Exists(session.FilePath))
                File.Delete(session.FilePath);
        }
        catch (Exception ex)
        {
            AppServices.LogError("删除会话记录文件失败", ex);
            AppServices.ShowError($"记录文件删除失败，会话记录已保留。\n{ex.Message}");
            return;
        }

        AppServices.Store.RemoveSession(session);
        AppServices.Audit.Add(AuditLevel.Warning, "record", $"删除会话记录 {session.DisplayPort}",
            session.FilePath);
        AppServices.NotifyDataChanged();
        RefreshAll();
    }

    private static SerialSessionRecord? SessionFrom(object sender)
        => (sender as Button)?.Tag as SerialSessionRecord;
}
