using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using FluentSerial.Core;

namespace FluentSerial.Features;

public partial class TerminalPage
{
    private void EmptyState_SizeChanged(object sender, SizeChangedEventArgs e)
        => EmptyDescription.Visibility = e.NewSize.Height < 150 ? Visibility.Collapsed : Visibility.Visible;

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        var expand = AdvancedPanel.Visibility != Visibility.Visible;
        AdvancedPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        AdvancedButton.Content = expand ? "收起参数 ▴" : "高级参数 ▾";
    }

    private void UpdateSignalControls()
    {
        var hardwareRts = ChoiceValue(HandshakeBox, Handshake.None) is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff;
        RtsCheck.IsEnabled = !hardwareRts;
        RtsCheck.ToolTip = hardwareRts ? "RTS 由硬件流控自动管理" : "手动控制 RTS 信号";
    }

    private void Handshake_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _loadingUi) return;
        UpdateSignalControls();
    }

    private void SendOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _loadingUi) return;
        Settings.DefaultEnding = ChoiceValue(EndingBox, LineEnding.None);
        Settings.DefaultChecksum = ChoiceValue(ChecksumBox, ChecksumKind.None);
        AppServices.Store.Save();
        UpdateSendSize();
    }

    private void AutoScroll_Click(object sender, RoutedEventArgs e)
    {
        Settings.AutoScroll = AutoScrollCheck.IsChecked == true;
        AppServices.Store.Save();
        if (Settings.AutoScroll) ConsoleBox.ScrollToEnd();
    }

    public void FocusConsoleSearch()
    {
        SearchBar.Visibility = Visibility.Visible;
        ConsoleSearchBox.Focus();
        ConsoleSearchBox.SelectAll();
        if (string.IsNullOrEmpty(ConsoleSearchBox.Text)) SearchResultText.Text = "输入关键词";
    }

    private void FindConsole_Click(object sender, RoutedEventArgs e) => FocusConsoleSearch();
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindConsoleMatch(-1);
    private void FindNext_Click(object sender, RoutedEventArgs e) => FindConsoleMatch(1);
    private void ConsoleSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        FindConsoleMatch(0);
    }

    private void CloseSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBar.Visibility = Visibility.Collapsed;
        ConsoleBox.Focus();
    }

    private void ConsoleSearch_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.ImeProcessed || e.ImeProcessedKey == Key.Enter) return;
        if (e.Key == Key.Escape)
        {
            CloseSearch_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            FindConsoleMatch(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    private void FindConsoleMatch(int direction)
    {
        var query = ConsoleSearchBox.Text;
        if (string.IsNullOrEmpty(query)) { SearchResultText.Text = "输入关键词"; return; }
        var matches = new List<(TextPointer Start, TextPointer End)>();
        // Console payloads are single Runs, so offsets map directly to text positions.
        foreach (var paragraph in _document.Blocks.OfType<Paragraph>())
        foreach (var run in paragraph.Inlines.OfType<Run>())
        {
            var offset = 0;
            while (offset <= run.Text.Length - query.Length)
            {
                var index = run.Text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                var start = run.ContentStart.GetPositionAtOffset(index);
                var end = run.ContentStart.GetPositionAtOffset(index + query.Length);
                if (start is not null && end is not null) matches.Add((start, end));
                offset = index + query.Length;
            }
        }
        if (matches.Count == 0) { SearchResultText.Text = "无匹配"; return; }
        var selected = ConsoleBox.Selection.Start;
        var target = direction switch
        {
            > 0 => matches.FindIndex(m => m.Start.CompareTo(selected) > 0),
            < 0 => matches.FindLastIndex(m => m.Start.CompareTo(selected) < 0),
            _ => 0
        };
        if (target < 0) target = direction < 0 ? matches.Count - 1 : 0;
        Settings.AutoScroll = false;
        AutoScrollCheck.IsChecked = false;
        ConsoleBox.Selection.Select(matches[target].Start, matches[target].End);
        (matches[target].Start.Parent as FrameworkContentElement)?.BringIntoView();
        SearchResultText.Text = $"{target + 1} / {matches.Count}";
    }

    public IReadOnlyList<(string Name, bool Ok, string Detail)> CheckSendExperienceForSmoke()
    {
        var checks = new List<(string, bool, string)>();
        void Check(string name, bool ok, string detail = "") => checks.Add((name, ok, detail));
        var originalBaud = BaudBox.Text;
        var originalText = SendBox.Text;
        var originalSettings = (Settings.SendMode, Settings.DefaultEnding, Settings.DefaultChecksum, Settings.AutoScroll);
        try
        {
            BaudBox.Text = "invalid";
            OpenCurrent();
            Check("非法波特率禁止打开", !AppServices.Serial.IsOpen && ConnectionErrorText.Text.Contains("波特率"));
            BaudBox.Text = originalBaud;
            TxTextRadio.IsChecked = true;
            SelectChoice(EndingBox, LineEnding.None);
            SelectChoice(ChecksumBox, ChecksumKind.None);
            SendBox.Text = "AB";
            SelectChoice(EndingBox, LineEnding.CrLf);
            SelectChoice(ChecksumBox, ChecksumKind.Sum8);
            Check("修改行尾校验即时更新预览", SendSizeText.Text == "5 B" && SendPreviewText.Text.Contains("41 42 83 0D 0A"), SendPreviewText.Text);
            SendBox.Clear();
            SelectChoice(ChecksumBox, ChecksumKind.None);
            Check("纯行尾发送与预览一致", TryGetSendPayload(out var endingOnly, out _) && endingOnly.SequenceEqual(new byte[] { 13, 10 }) && SendSizeText.Text == "2 B");
            TxHexRadio.IsChecked = true;
            SendBox.Text = "0xGG";
            Check("无效 HEX 明确提示且禁发", !SendButton.IsEnabled && SendPreviewText.Text.Contains("无效"), SendPreviewText.Text);
            SendBox.Text = "草稿";
            _sendHistory.Clear();
            _sendHistory.AddRange(new[] { "AT", "ATI" });
            _sendHistoryIndex = -1;
            RecallHistory(-1);
            RecallHistory(1);
            Check("发送历史恢复未发送草稿", SendBox.Text == "草稿");
            ResetConsole();
            AddSystemLine("find-target FIND-TARGET", ConsoleTone.Muted);
            FlushRenderQueue();
            FocusConsoleSearch();
            ConsoleSearchBox.Text = "find-target";
            FindConsoleMatch(1);
            Check("控制台查找及下一个匹配", SearchResultText.Text == "2 / 2" && ConsoleBox.Selection.Text == "FIND-TARGET", SearchResultText.Text);
            FindConsoleMatch(1);
            Check("查找循环回到首项", SearchResultText.Text == "1 / 2");
        }
        finally
        {
            BaudBox.Text = originalBaud;
            if (originalSettings.SendMode == PayloadMode.Hex) TxHexRadio.IsChecked = true; else TxTextRadio.IsChecked = true;
            SelectChoice(EndingBox, originalSettings.DefaultEnding);
            SelectChoice(ChecksumBox, originalSettings.DefaultChecksum);
            Settings.AutoScroll = originalSettings.AutoScroll;
            AutoScrollCheck.IsChecked = Settings.AutoScroll;
            SendBox.Text = originalText;
            ConsoleSearchBox.Clear();
            SearchBar.Visibility = Visibility.Collapsed;
            ConnectionErrorText.Visibility = Visibility.Collapsed;
            ResetConsole();
        }
        return checks;
    }

    public IReadOnlyList<(string Name, bool Ok, string Detail)> CheckLayoutForSmoke(MainWindow window)
    {
        var width = window.Width;
        var height = window.Height;
        var advanced = AdvancedPanel.Visibility;
        try
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            AdvancedPanel.Visibility = Visibility.Visible;
            window.UpdateLayout();
            var controls = new FrameworkElement[] { PortBox, BaudBox, EncodingBox, HandshakeBox, BreakButton, SendButton, SendFileButton, AutoSendIntervalBox };
            var allInside = controls.All(control =>
            {
                var bounds = control.TransformToAncestor(this).TransformBounds(new Rect(control.RenderSize));
                return bounds.Left >= 0 && bounds.Right <= ActualWidth + 1 && bounds.Top >= 0 && bounds.Bottom <= ActualHeight + 1;
            });
            NativeScreenshot.Capture(window, System.IO.Path.Combine(AppServices.Paths.Root, "terminal-compact.png"));
            return new[]
            {
                ("最小窗口高级参数和发送控件可见", allInside, $"{window.ActualWidth} × {window.ActualHeight}"),
                ("最小窗口接收区保留阅读空间", ConsoleBox.ActualHeight >= 72, $"{ConsoleBox.ActualHeight:0} DIP")
            };
        }
        finally
        {
            window.Width = width;
            window.Height = height;
            AdvancedPanel.Visibility = advanced;
            window.UpdateLayout();
        }
    }
}
