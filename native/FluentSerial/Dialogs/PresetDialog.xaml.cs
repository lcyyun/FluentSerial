using System.Globalization;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using FluentSerial.Core;

namespace FluentSerial.Dialogs;

public partial class PresetDialog : Window
{
    private static readonly int[] CommonBaudRates =
    {
        9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600
    };

    private readonly IReadOnlyList<PortProfile> _existing;
    private readonly PortProfile? _editing;

    /// <summary>The created or updated profile, available after the dialog returns true.</summary>
    public PortProfile? Result { get; private set; }

    public PresetDialog(IEnumerable<PortProfile> existing, PortProfile? edit)
    {
        _existing = existing.ToList();
        _editing = edit;
        InitializeComponent();
        Title = edit is null ? "新建预设" : "编辑预设";
        DialogTitle.Text = Title;

        foreach (var rate in CommonBaudRates) BaudBox.Items.Add(rate);
        RebuildPortItems(edit?.PortName);

        var values = edit ?? CreateDefaults();
        NameBox.Text = edit?.Name ?? string.Empty;
        BaudBox.Text = values.BaudRate.ToString(CultureInfo.InvariantCulture);
        SelectTag(DataBitsBox, values.DataBits.ToString(CultureInfo.InvariantCulture));
        SelectTag(ParityBox, values.Parity.ToString());
        SelectTag(StopBitsBox, values.StopBits.ToString());
        SelectTag(FlowBox, values.Handshake.ToString());
        SelectTag(ReceiveModeBox, values.ReceiveMode.ToString());
        SelectTag(SendModeBox, values.SendMode.ToString());
        SelectTag(EncodingBox, values.Encoding);
        DtrCheck.IsChecked = values.Dtr;
        RtsCheck.IsChecked = values.Rts;
        FavoriteCheck.IsChecked = values.Favorite;
        NotesBox.Text = values.Notes;

        Loaded += (_, _) => NameBox.Focus();
    }

    private static PortProfile CreateDefaults()
    {
        var settings = AppServices.Settings;
        return new PortProfile
        {
            BaudRate = settings.LastBaudRate,
            DataBits = settings.LastDataBits,
            Parity = settings.LastParity,
            StopBits = settings.LastStopBits,
            Handshake = settings.LastHandshake,
            Dtr = settings.LastDtr,
            Rts = settings.LastRts,
            ReceiveMode = settings.ReceiveMode,
            SendMode = settings.SendMode,
            Encoding = settings.DefaultEncoding
        };
    }

    private void RebuildPortItems(string? preferredPort)
    {
        PortBox.Items.Clear();
        PortBox.Items.Add(new ComboBoxItem { Content = "自动选择", Tag = string.Empty });
        foreach (var port in SerialPortCatalog.GetPorts())
            PortBox.Items.Add(new ComboBoxItem { Content = port.Label, Tag = port.Name });

        if (!string.IsNullOrWhiteSpace(preferredPort) &&
            !PortBox.Items.OfType<ComboBoxItem>()
                .Any(item => string.Equals(item.Tag as string, preferredPort, StringComparison.Ordinal)))
        {
            PortBox.Items.Add(new ComboBoxItem { Content = $"{preferredPort}（当前不可用）", Tag = preferredPort });
        }

        SelectTag(PortBox, preferredPort ?? string.Empty);
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

    private static T SelectedEnum<T>(ComboBox box, T fallback) where T : struct, Enum
        => Enum.TryParse<T>((box.SelectedItem as ComboBoxItem)?.Tag as string, out var value) ? value : fallback;

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        var selected = (PortBox.SelectedItem as ComboBoxItem)?.Tag as string;
        RebuildPortItems(selected);
    }

    private bool Validate(out string error)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            error = "请输入预设名称。";
            return false;
        }

        if (_existing.Any(p => !ReferenceEquals(p, _editing) &&
                               string.Equals(p.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)))
        {
            error = $"已存在名为「{name}」的预设，请换一个名称。";
            return false;
        }

        if (!int.TryParse(BaudBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var baud) ||
            baud < 1 || baud > 4_000_000)
        {
            error = "波特率必须是 1 到 4000000 之间的整数。";
            return false;
        }

        if (DataBitsBox.SelectedItem is not ComboBoxItem { Tag: string bitsTag } ||
            !int.TryParse(bitsTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) ||
            bits < 5 || bits > 8)
        {
            error = "数据位必须在 5 到 8 之间。";
            return false;
        }

        if (ParityBox.SelectedItem is not ComboBoxItem) { error = "请选择校验方式。"; return false; }
        if (StopBitsBox.SelectedItem is not ComboBoxItem) { error = "请选择停止位。"; return false; }
        if (FlowBox.SelectedItem is not ComboBoxItem) { error = "请选择流控方式。"; return false; }
        if (ReceiveModeBox.SelectedItem is not ComboBoxItem) { error = "请选择接收模式。"; return false; }
        if (SendModeBox.SelectedItem is not ComboBoxItem) { error = "请选择发送模式。"; return false; }
        if (EncodingBox.SelectedItem is not ComboBoxItem) { error = "请选择编码。"; return false; }

        error = string.Empty;
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate(out var error))
        {
            ValidationText.Text = error;
            ValidationText.Visibility = Visibility.Visible;
            return;
        }
        ValidationText.Visibility = Visibility.Collapsed;

        var profile = _editing ?? new PortProfile();
        profile.Name = NameBox.Text.Trim();
        profile.PortName = (PortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
        profile.BaudRate = int.Parse(BaudBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        profile.DataBits = int.Parse(((ComboBoxItem)DataBitsBox.SelectedItem!).Tag!.ToString()!,
            NumberStyles.Integer, CultureInfo.InvariantCulture);
        profile.Parity = SelectedEnum(ParityBox, Parity.None);
        profile.StopBits = SelectedEnum(StopBitsBox, StopBits.One);
        profile.Handshake = SelectedEnum(FlowBox, Handshake.None);
        profile.ReceiveMode = SelectedEnum(ReceiveModeBox, PayloadMode.Text);
        profile.SendMode = SelectedEnum(SendModeBox, PayloadMode.Text);
        profile.Encoding = (EncodingBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "utf-8";
        profile.Dtr = DtrCheck.IsChecked == true;
        profile.Rts = RtsCheck.IsChecked == true;
        profile.Favorite = FavoriteCheck.IsChecked == true;
        profile.Notes = NotesBox.Text.Trim();

        if (_editing is null) AppServices.Data.Profiles.Add(profile);

        AppServices.Audit.Add(AuditLevel.Info, "profile",
            _editing is null ? $"新建端口预设 {profile.Name}" : $"更新端口预设 {profile.Name}",
            $"{profile.PortLabel}  {profile.ParamsLabel}  {profile.FlowLabel}");
        AppServices.NotifyDataChanged();

        Result = profile;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
