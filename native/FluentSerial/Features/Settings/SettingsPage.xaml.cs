using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FluentSerial.Core;

namespace FluentSerial.Features;

public partial class SettingsPage : UserControl, IActivatablePage
{
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsPage()
    {
        InitializeComponent();
        ConfigureChoices();
        LoadFromSettings(AppServices.Settings);
    }

    public void OnActivated()
    {
        LoadFromSettings(AppServices.Settings);
        StatusText.Text = "本地设置";
    }

    private void ConfigureChoices()
    {
        ThemeBox.ItemsSource = new List<Choice<string>>
        {
            new("Light", "浅色"),
            new("Dark", "深色"),
            new("System", "跟随系统")
        };
        EncodingBox.ItemsSource = new List<Choice<string>>
        {
            new("utf-8", "UTF-8"),
            new("gbk", "GBK"),
            new("ascii", "ASCII"),
            new("iso-8859-1", "Latin-1")
        };
        ReceiveModeBox.ItemsSource = new List<Choice<PayloadMode>>
        {
            new(PayloadMode.Text, "文本"),
            new(PayloadMode.Hex, "HEX")
        };
        EndingBox.ItemsSource = new List<Choice<LineEnding>>
        {
            new(LineEnding.None, "无"),
            new(LineEnding.Cr, "CR"),
            new(LineEnding.Lf, "LF"),
            new(LineEnding.CrLf, "CRLF")
        };
        ChecksumBox.ItemsSource = new List<Choice<ChecksumKind>>
        {
            new(ChecksumKind.None, "无"),
            new(ChecksumKind.Sum8, "SUM8"),
            new(ChecksumKind.Xor8, "XOR8"),
            new(ChecksumKind.Crc16Modbus, "CRC16-Modbus"),
            new(ChecksumKind.Crc16Ccitt, "CRC16-CCITT")
        };
    }

    private void LoadFromSettings(AppSettings settings)
    {
        SelectChoice(ThemeBox, NormalizeTheme(settings.Theme));
        SelectChoice(EncodingBox, settings.DefaultEncoding);
        SelectChoice(ReceiveModeBox, settings.ReceiveMode);
        SelectChoice(EndingBox, settings.DefaultEnding);
        SelectChoice(ChecksumBox, settings.DefaultChecksum);
        FontFamilyBox.Text = settings.FontFamily;
        FontSizeBox.Text = settings.ConsoleFontSize.ToString(CultureInfo.InvariantCulture);
        MaxLinesBox.Text = settings.MaxLines.ToString(CultureInfo.InvariantCulture);
        AutoSendIntervalBox.Text = settings.AutoSendIntervalMs.ToString(CultureInfo.InvariantCulture);
        ShowTimestampCheck.IsChecked = settings.ShowTimestamp;
        ShowTxCheck.IsChecked = settings.ShowTx;
        AutoScrollCheck.IsChecked = settings.AutoScroll;
        ConfirmClearCheck.IsChecked = settings.ConfirmBeforeClear;
        UseEscapesCheck.IsChecked = settings.UseEscapes;
        AutoRecordCheck.IsChecked = settings.AutoRecord;
        DataPathText.Text = AppServices.Paths.Root;
    }

    private static string NormalizeTheme(string theme)
        => theme is "Light" or "Dark" or "System" ? theme : "Light";

    private static void SelectChoice<T>(ComboBox box, T value)
    {
        foreach (var item in box.Items)
        {
            if (item is Choice<T> choice && EqualityComparer<T>.Default.Equals(choice.Value, value))
            {
                box.SelectedItem = item;
                return;
            }
        }
    }

    private static T ChoiceValue<T>(ComboBox box, T fallback)
        => box.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var fontSizeText = FontSizeBox.Text.Trim();
        if (!double.TryParse(fontSizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize) &&
            !double.TryParse(fontSizeText, out fontSize))
        {
            Fail("控制台字号必须是数字，范围 9-24");
            return;
        }
        if (fontSize < 9 || fontSize > 24)
        {
            Fail("控制台字号需在 9-24 之间");
            return;
        }
        if (!int.TryParse(MaxLinesBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxLines) ||
            maxLines < 200 || maxLines > 20000)
        {
            Fail("控制台最大行数需在 200-20000 之间");
            return;
        }
        if (!int.TryParse(AutoSendIntervalBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval) ||
            interval < 50 || interval > 60000)
        {
            Fail("定时发送间隔需在 50-60000 ms 之间");
            return;
        }

        var settings = AppServices.Settings;
        settings.Theme = NormalizeTheme(ChoiceValue(ThemeBox, "Light"));
        settings.FontFamily = string.IsNullOrWhiteSpace(FontFamilyBox.Text) ? "Cascadia Mono" : FontFamilyBox.Text.Trim();
        settings.ConsoleFontSize = fontSize;
        settings.DefaultEncoding = ChoiceValue(EncodingBox, "utf-8");
        settings.ReceiveMode = ChoiceValue(ReceiveModeBox, PayloadMode.Text);
        settings.DefaultEnding = ChoiceValue(EndingBox, LineEnding.CrLf);
        settings.DefaultChecksum = ChoiceValue(ChecksumBox, ChecksumKind.None);
        settings.MaxLines = maxLines;
        settings.AutoSendIntervalMs = interval;
        settings.ShowTimestamp = ShowTimestampCheck.IsChecked == true;
        settings.ShowTx = ShowTxCheck.IsChecked == true;
        settings.AutoScroll = AutoScrollCheck.IsChecked == true;
        settings.ConfirmBeforeClear = ConfirmClearCheck.IsChecked == true;
        settings.UseEscapes = UseEscapesCheck.IsChecked == true;
        settings.AutoRecord = AutoRecordCheck.IsChecked == true;

        AppServices.Store.Save();
        ThemeService.Apply(settings.Theme);
        AppServices.MainWindow?.RefreshThemeUi();
        AppServices.MainWindow?.Terminal?.ApplySettings();
        AppServices.Audit.Add(AuditLevel.Info, "settings", "更新应用设置");
        StatusText.Text = $"已保存  {DateTime.Now:HH:mm:ss}";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppSuccessBrush");
    }

    private void Fail(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppDangerBrush");
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        LoadFromSettings(new AppSettings());
        StatusText.Text = "已填入默认值，保存后生效";
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("AppTextTertiaryBrush");
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => AppServices.OpenInExplorer(AppServices.Paths.Root);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => AppServices.OpenInExplorer(AppServices.Paths.LogsDirectory);

    // ---------------------------------------------------------------- UI smoke hooks

    public string SaveStatusForSmoke => StatusText.Text;

    /// <summary>Invokes the same save handler as the visible button for --ui-smoke.</summary>
    public void SaveForSmoke() => Save_Click(this, new RoutedEventArgs());
}
