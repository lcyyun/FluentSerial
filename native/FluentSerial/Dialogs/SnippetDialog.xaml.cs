using System.Windows;
using System.Windows.Controls;
using FluentSerial.Core;

namespace FluentSerial.Dialogs;

public partial class SnippetDialog : Window
{
    private readonly QuickCommand? _editing;

    /// <summary>The created or updated command, available after the dialog returns true.</summary>
    public QuickCommand? Result { get; private set; }

    public SnippetDialog(QuickCommand? edit)
    {
        _editing = edit;
        InitializeComponent();
        Title = edit is null ? "新建指令" : "编辑指令";
        DialogTitle.Text = Title;

        BuildGroupItems(edit?.Group);
        SelectTag(EndingBox, (edit?.Ending ?? AppServices.Settings.DefaultEnding).ToString());
        SelectTag(ChecksumBox, (edit?.Checksum ?? AppServices.Settings.DefaultChecksum).ToString());

        if (edit is not null)
        {
            NameBox.Text = edit.Name;
            PayloadBox.Text = edit.Payload;
            DescriptionBox.Text = edit.Description;
            FavoriteCheck.IsChecked = edit.Favorite;
            if (edit.Mode == PayloadMode.Hex) HexModeRadio.IsChecked = true;
            else TextModeRadio.IsChecked = true;
        }
        else
        {
            GroupBox.Text = "自定义";
            TextModeRadio.IsChecked = true;
        }

        UpdatePreview();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void BuildGroupItems(string? current)
    {
        var groups = AppServices.Data.Commands
            .Select(c => c.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.CurrentCulture)
            .OrderBy(g => g, StringComparer.CurrentCulture)
            .ToList();
        if (!string.IsNullOrWhiteSpace(current) && !groups.Contains(current, StringComparer.CurrentCulture))
            groups.Insert(0, current);
        if (!groups.Contains("自定义", StringComparer.CurrentCulture)) groups.Add("自定义");

        foreach (var group in groups) GroupBox.Items.Add(group);
        GroupBox.Text = string.IsNullOrWhiteSpace(current) ? "自定义" : current;
    }

    private static void SelectTag(ComboBox box, string? tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private PayloadMode CurrentMode => HexModeRadio.IsChecked == true ? PayloadMode.Hex : PayloadMode.Text;

    private LineEnding CurrentEnding =>
        Enum.TryParse<LineEnding>((EndingBox.SelectedItem as ComboBoxItem)?.Tag as string, out var value)
            ? value
            : LineEnding.None;

    private ChecksumKind CurrentChecksum =>
        Enum.TryParse<ChecksumKind>((ChecksumBox.SelectedItem as ComboBoxItem)?.Tag as string, out var value)
            ? value
            : ChecksumKind.None;

    private void PayloadBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void Mode_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewText is null || SaveButton is null) return;

        var mode = CurrentMode;
        var ok = SerialCodec.TryBuildPayload(PayloadBox.Text, mode, CurrentEnding, CurrentChecksum,
            AppServices.Settings.DefaultEncoding, AppServices.Settings.UseEscapes,
            out var payload, out var error);

        if (!ok)
        {
            PreviewText.Text = "载荷预览不可用";
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            SaveButton.IsEnabled = false;
            return;
        }

        SaveButton.IsEnabled = true;
        ValidationText.Visibility = Visibility.Collapsed;

        if (payload.Length == 0)
        {
            PreviewText.Text = "0 字节    空载荷";
            return;
        }

        var hex = SerialCodec.FormatHex(payload);
        if (hex.Length > 120) hex = hex[..120] + " …";
        PreviewText.Text = $"{payload.Length} 字节    {hex}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowValidation("请输入指令名称。");
            return;
        }

        var mode = CurrentMode;
        if (!SerialCodec.TryBuildPayload(PayloadBox.Text, mode, CurrentEnding, CurrentChecksum,
                AppServices.Settings.DefaultEncoding, AppServices.Settings.UseEscapes,
                out var payload, out var error))
        {
            ShowValidation(error ?? "载荷无法发送。");
            return;
        }
        if (payload.Length == 0)
        {
            ShowValidation("载荷不能为空。");
            return;
        }
        ValidationText.Visibility = Visibility.Collapsed;

        var command = _editing ?? new QuickCommand();
        command.Name = name;
        command.Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "自定义" : GroupBox.Text.Trim();
        command.Mode = mode;
        command.Ending = CurrentEnding;
        command.Checksum = CurrentChecksum;
        command.Payload = PayloadBox.Text;
        command.Description = DescriptionBox.Text.Trim();
        command.Favorite = FavoriteCheck.IsChecked == true;

        if (_editing is null) AppServices.Data.Commands.Add(command);

        AppServices.Audit.Add(AuditLevel.Info, "system",
            _editing is null ? $"新建快捷指令 {command.Name}" : $"更新快捷指令 {command.Name}",
            SerialCodec.DescribePayload(payload, mode, AppServices.Settings.DefaultEncoding));
        AppServices.NotifyDataChanged();

        Result = command;
        DialogResult = true;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
