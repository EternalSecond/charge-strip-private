using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Xml.Serialization;

public sealed class StripSettings
{
    public bool UseExistingIsland;
    internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Charge Strip");
    internal static readonly string FileName = Path.Combine(Folder, "settings.xml");

    public int Zoom = 100, Gap = 5, Alpha = 85;
    public string[] HiddenDevices = new string[0], DeviceOrder = new string[0];
    public int MaxDevices = 5, DisplayMode;
    public string Language = "uk";
    public bool ResourcesEnabled = true, ResourcesIconOnly;
    public ResourceMetric[] ResourceStripOrder;
    public ResourceMetric[] ResourcePanelOrder = (ResourceMetric[])ResourceMetrics.DefaultOrder.Clone();
    public ResourceMetric HiddenStripMetrics = ResourceMetrics.All & ~ResourceMetric.Cpu, HiddenPanelMetrics;
    public bool ResourceScales;
    public string ResourceScaleColor;

    internal ResourceMetric[] ResourceOrder(bool onStrip)
    {
        return (onStrip ? ResourceStripOrder : ResourcePanelOrder) ?? ResourceMetrics.DefaultOrder;
    }

    internal ResourceMetric HiddenMetrics(bool onStrip)
    {
        if (!onStrip) return HiddenPanelMetrics;
        return ResourceStripOrder == null && ResourcesIconOnly ? ResourceMetrics.All : HiddenStripMetrics;
    }

    internal IEnumerable<ResourceMetric> VisibleMetrics(bool onStrip)
    {
        ResourceMetric hidden = HiddenMetrics(onStrip);
        return ResourceOrder(onStrip).Where(metric => (hidden & metric) == 0);
    }

    internal Color ScaleColor { get { return ColorOf(ResourceScaleColor ?? Icons); } }

    internal static readonly string[] ModuleIds = { "batteries", "media", "resources" };
    public string[] ModuleOrder = (string[])ModuleIds.Clone();
    public string[] HiddenModules = new string[0];
    public bool? DockToTray;
    public int? StripX, StripY;
    public string StripMonitor = "";
    internal bool IsDocked { get { return DockToTray ?? !StripX.HasValue; } }
    // Old overlay settings are read for migration.
    [XmlElement("X")] public int? LegacyX;
    [XmlElement("Y")] public int? LegacyY;
    [XmlElement("Monitor")] public string LegacyMonitor;
    [XmlElement("Automatic")] public bool LegacyAutomatic = true;
    public bool ShouldSerializeLegacyX() { return false; }
    public bool ShouldSerializeLegacyY() { return false; }
    public bool ShouldSerializeLegacyMonitor() { return false; }
    public bool ShouldSerializeLegacyAutomatic() { return false; }

    internal bool ModuleVisible(string id)
    {
        return (id != "resources" || ResourcesEnabled) && !HiddenModules.Contains(id);
    }

    internal static string ModuleLabel(string id)
    {
        return id == "batteries" ? "Батареї" : id == "resources" ? "Ресурси ПК" : "Медіа";
    }

    internal List<BatteryReading> Selected(IEnumerable<BatteryReading> readings)
    {
        return Ordered(readings).Where(row => !HiddenDevices.Contains(row.Name)).ToList();
    }

    internal IEnumerable<BatteryReading> Ordered(IEnumerable<BatteryReading> readings)
    {
        return readings.OrderBy(row =>
        {
            int index = Array.IndexOf(DeviceOrder, row.Name);
            return index < 0 ? int.MaxValue : index;
        });
    }

    public string Background = "#1F232B", Text = "#F0F3F7", Icons = "#9BE1C2", Low = "#FFBD78";

    internal StripSettings Copy() { return (StripSettings)MemberwiseClone(); }
    internal static int Clamp(int value, int min, int max) { return Math.Min(max, Math.Max(min, value)); }

    internal static bool TryColor(string hex, out Color color)
    {
        color = Color.Empty;
        int rgb;
        if (hex == null || hex.Length != 7 || hex[0] != '#' ||
            !int.TryParse(hex.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb)) return false;
        color = Color.FromArgb(rgb | unchecked((int)0xff000000));
        return true;
    }

    internal static Color PopupColor(string hex, int tint)
    {
        var color = ColorOf(hex);
        return Color.FromArgb(24 + (color.R - 24) * tint / 100,
                              28 + (color.G - 28) * tint / 100,
                              36 + (color.B - 36) * tint / 100);
    }

    internal static Color ColorOf(string hex)
    {
        Color color;
        TryColor(hex, out color);
        return color;
    }

    internal static StripSettings Load()
    {
        try
        {
            using (var file = File.OpenRead(FileName))
            {
                var settings = (StripSettings)new XmlSerializer(typeof(StripSettings)).Deserialize(file);
                settings.Zoom = Clamp(settings.Zoom, 60, 175);
                settings.Gap = Clamp(settings.Gap, 0, 24);
                settings.Alpha = Clamp(settings.Alpha, 35, 100);
                if (Array.IndexOf(Localize.Languages, settings.Language) < 0) settings.Language = "uk";
                settings.ModuleOrder = (settings.ModuleOrder ?? ModuleIds).Where(ModuleIds.Contains).Distinct().Concat(ModuleIds).Distinct().ToArray();
                settings.HiddenModules = (settings.HiddenModules ?? new string[0]).Where(ModuleIds.Contains).Distinct().ToArray();
                if (!settings.StripX.HasValue && !settings.LegacyAutomatic && settings.LegacyX.HasValue)
                {
                    settings.StripX = settings.LegacyX;
                    settings.StripY = settings.LegacyY;
                    settings.StripMonitor = settings.LegacyMonitor ?? "";
                }
                settings.MaxDevices = Clamp(settings.MaxDevices, 1, 12);
                settings.DisplayMode = Clamp(settings.DisplayMode, 0, 2);
                settings.HiddenDevices = settings.HiddenDevices ?? new string[0];
                settings.DeviceOrder = settings.DeviceOrder ?? new string[0];
                var defaults = new StripSettings();
                Color color;
                if (!TryColor(settings.Background, out color)) settings.Background = defaults.Background;
                if (!TryColor(settings.Text, out color)) settings.Text = defaults.Text;
                if (!TryColor(settings.Icons, out color)) settings.Icons = defaults.Icons;
                if (!TryColor(settings.Low, out color)) settings.Low = defaults.Low;
                settings.HiddenStripMetrics = settings.HiddenMetrics(true) & ResourceMetrics.All;
                settings.HiddenPanelMetrics &= ResourceMetrics.All;
                settings.ResourceStripOrder = ResourceMetrics.Normalize(settings.ResourceStripOrder);
                settings.ResourcePanelOrder = ResourceMetrics.Normalize(settings.ResourcePanelOrder);
                if (!TryColor(settings.ResourceScaleColor, out color)) settings.ResourceScaleColor = null;
                return settings;
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal void Save()
    {
        Directory.CreateDirectory(Folder);
        string pending = FileName + ".tmp";
        using (var file = File.Create(pending))
            new XmlSerializer(typeof(StripSettings)).Serialize(file, this);
        if (File.Exists(FileName)) File.Replace(pending, FileName, null);
        else File.Move(pending, FileName);
    }
}

internal static class StartupRegistration
{
    internal static string ShortcutPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Charge Strip.lnk"); }
    }

    internal static bool Enabled { get { return File.Exists(ShortcutPath); } }

    internal static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(ShortcutPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath));
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        try
        {
            dynamic shortcut = shell.CreateShortcut(ShortcutPath);
            try
            {
                shortcut.TargetPath = Application.ExecutablePath;
                shortcut.Arguments = "";
                shortcut.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);
                shortcut.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    internal static bool IsAccessError(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException ||
               error is System.Security.SecurityException || error is System.Runtime.InteropServices.COMException;
    }
}

internal sealed class ResourceEditor : UserControl
{
    private readonly Strip owner;
    private readonly CheckedListBox stripList, panelList;
    private readonly ComboBox mode = new ComboBox { Width = 184, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox hex = new TextBox { Width = 91, MaxLength = 7, AccessibleName = "Колір шкал" };
    private readonly Control preview;
    private bool loading;

    internal ResourceEditor(Strip owner, Action validate)
    {
        this.owner = owner;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 328));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        stripList = AddList(options, "На смужці", true);
        panelList = AddList(options, "У панелі", false);
        options.Controls.Add(Heading("Вигляд панелі"));
        mode.AccessibleName = "Вигляд панелі";
        options.Controls.Add(mode);
        options.Controls.Add(Heading("Колір шкал"));
        var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var palette = new Button { Text = "Палітра…", AutoSize = true };
        colorRow.Controls.AddRange(new Control[] { hex, palette });
        options.Controls.Add(colorRow);
        layout.Controls.Add(options, 0, 0);

        var previewArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        previewArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewArea.Controls.Add(Heading("Попередній перегляд"));
        preview = new ResourcePreview(owner) { Dock = DockStyle.Fill };
        previewArea.Controls.Add(preview);
        layout.Controls.Add(previewArea, 1, 0);
        Controls.Add(layout);

        mode.SelectedIndexChanged += (sender, args) =>
        {
            if (loading || mode.SelectedIndex < 0) return;
            owner.Current.ResourceScales = mode.SelectedIndex == 1;
            owner.ApplySettings();
        };
        hex.TextChanged += (sender, args) =>
        {
            if (loading) return;
            Color color;
            if (StripSettings.TryColor(hex.Text, out color))
            {
                owner.Current.ResourceScaleColor = hex.Text.ToUpperInvariant();
                owner.ApplySettings();
            }
            validate();
        };
        palette.Click += (sender, args) =>
        {
            using (var dialog = new ColorDialog { Color = owner.Current.ScaleColor, FullOpen = true })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    hex.Text = string.Format("#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B);
        };
    }

    private static Label Heading(string caption)
    {
        return new Label { Text = caption, AutoSize = true, Margin = new Padding(2, 9, 0, 4) };
    }

    private CheckedListBox AddList(FlowLayoutPanel options, string caption, bool onStrip)
    {
        options.Controls.Add(Heading(caption));
        var group = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        var list = new MetricList { Width = 236, Height = 114, IntegralHeight = false, FormattingEnabled = true,
            AccessibleName = caption };
        var arrows = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Margin = Padding.Empty };
        foreach (int delta in new[] { -1, 1 })
        {
            var arrow = new Button { Text = delta < 0 ? "↑" : "↓", AccessibleName = delta < 0 ? "Вище" : "Нижче",
                Width = 37, Height = 26 };
            arrow.Click += (sender, args) =>
            {
                int source = list.SelectedIndex, target = source + delta;
                if (source < 0 || target < 0 || target >= list.Items.Count) return;
                var order = (ResourceMetric[])owner.Current.ResourceOrder(onStrip).Clone();
                ResourceMetric selected = order[source];
                order[source] = order[target];
                order[target] = selected;
                if (onStrip) owner.Current.ResourceStripOrder = order;
                else owner.Current.ResourcePanelOrder = order;
                Fill(list, onStrip, selected);
                owner.ApplySettings();
            };
            arrows.Controls.Add(arrow);
        }
        list.Format += (sender, args) => args.Value = owner.Translate(ResourceMetrics.Label((ResourceMetric)args.ListItem));
        list.ItemCheck += (sender, args) =>
        {
            if (loading) return;
            var settings = owner.Current;
            ResourceMetric hidden = settings.HiddenMetrics(onStrip);
            ResourceMetric item = (ResourceMetric)list.Items[args.Index];
            hidden = args.NewValue == CheckState.Checked ? hidden & ~item : hidden | item;
            if (onStrip)
            {
                settings.ResourceStripOrder = settings.ResourceOrder(true);
                settings.HiddenStripMetrics = hidden;
            }
            else settings.HiddenPanelMetrics = hidden;
            owner.ApplySettings();
        };
        group.Controls.AddRange(new Control[] { list, arrows });
        options.Controls.Add(group);
        return list;
    }

    private void Fill(CheckedListBox list, bool onStrip, object selected)
    {
        bool wasLoading = loading;
        loading = true;
        list.BeginUpdate();
        list.Items.Clear();
        var settings = owner.Current;
        foreach (ResourceMetric metric in settings.ResourceOrder(onStrip))
            list.Items.Add(metric, (settings.HiddenMetrics(onStrip) & metric) == 0);
        list.SelectedItem = selected;
        list.EndUpdate();
        loading = wasLoading;
    }

    internal void Sync()
    {
        loading = true;
        Fill(stripList, true, stripList.SelectedItem);
        Fill(panelList, false, panelList.SelectedItem);
        mode.Items.Clear();
        mode.Items.AddRange(new object[] { owner.Translate("Цифри"), owner.Translate("Цифри + шкала") });
        mode.SelectedIndex = owner.Current.ResourceScales ? 1 : 0;
        hex.Text = owner.Current.ResourceScaleColor ?? owner.Current.Icons;
        loading = false;
        RefreshPreview();
    }

    internal bool ValidateColor()
    {
        Color unused;
        bool valid = StripSettings.TryColor(hex.Text, out unused);
        hex.BackColor = valid ? Color.FromArgb(35, 45, 61) : Color.FromArgb(99, 40, 50);
        return valid;
    }

    internal void RefreshPreview()
    {
        if (!loading && owner.Current.ResourceScaleColor == null && ValidateColor())
        {
            loading = true;
            hex.Text = owner.Current.Icons;
            loading = false;
        }
        preview.Invalidate();
    }

    private sealed class MetricList : CheckedListBox
    {
        private bool selecting;

        internal MetricList() { CheckOnClick = true; }

        // A click on the name selects a row for the arrows; the check box and Space change visibility.
        protected override void WndProc(ref Message message)
        {
            bool mouse = message.Msg == 0x0201 || message.Msg == 0x0202 || message.Msg == 0x0203;
            bool previous = selecting;
            if (mouse) selecting = unchecked((short)message.LParam.ToInt64()) >= 18 * DeviceDpi / 96;
            try { base.WndProc(ref message); }
            finally { selecting = previous; }
        }

        protected override void OnItemCheck(ItemCheckEventArgs e)
        {
            if (selecting) e.NewValue = e.CurrentValue;
            else base.OnItemCheck(e);
        }
    }

    private sealed class ResourcePreview : Control
    {
        private readonly Strip owner;
        internal ResourcePreview(Strip owner) { this.owner = owner; DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var settings = owner.Current;
            if (!settings.ModuleVisible("resources"))
            {
                TextRenderer.DrawText(e.Graphics, owner.Translate("Модуль вимкнено"), Font, new Point(0, 8), ForeColor);
                return;
            }
            var reading = owner.ResourceReading;
            int count = settings.VisibleMetrics(true).Count();
            int width = (int)ResourcesModule.TileWidth(count, false) + 12;
            var tile = new Rectangle(0, 8, width, 34);
            using (var background = new SolidBrush(StripSettings.PopupColor(settings.Background, settings.Alpha)))
                e.Graphics.FillRectangle(background, tile);
            ResourcesModule.DrawTile(e.Graphics, new RectangleF(6, 10, width - 12, 30), reading, settings);
            ResourcePanel.Draw(e.Graphics, new Rectangle(0, 60, Width, Math.Max(0, Height - 60)), reading, settings);
        }
    }
}

internal sealed class SettingsWindow : Form
{
    private readonly CheckBox existingIsland = new CheckBox { Text = "Вбудувати в наявний Island Media Controls", AutoSize = true };
    private readonly Strip strip;
    private readonly NumericUpDown zoom = Number("Масштаб", 60, 175), gap = Number("Відступ", 0, 24),
        alpha = Number("Фон", 35, 100);
    private readonly CheckBox startup = new CheckBox { Text = "Запускати разом із Windows", AutoSize = true };
    private readonly CheckedListBox modules = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, Width = 217, Height = 62 };
    private readonly CheckedListBox devices = new CheckedListBox { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly ComboBox displayMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    private readonly NumericUpDown maxDevices = Number("Ліміт пристроїв", 1, 12);
    private readonly ComboBox language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly List<Action<string>> captions = new List<Action<string>>();
    private readonly Button save = ButtonFor("Зберегти");
    private readonly Label error = new Label { AutoSize = true, ForeColor = Color.Firebrick };
    private static readonly string[] ColorNames = { "Фон", "Текст", "Іконки", "Низький заряд" };
    private readonly TextBox[] hex = new TextBox[4];
    private readonly Button[] palette = new Button[4];
    private readonly Preview preview;
    private readonly TabPage generalPage = new TabPage(), resourcePage = new TabPage(), positionPage = new TabPage();
    private readonly ComboBox positionMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 254 };
    private readonly ComboBox positionMonitor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 254 };
    private readonly NumericUpDown positionX = Number("X", 0, 0), positionY = Number("Y", 0, 0);
    private Screen[] monitors;
    private readonly ResourceEditor resourceEditor;
    private bool loading;

    internal SettingsWindow(Strip owner)
    {
        strip = owner;
        preview = new Preview(owner) { Dock = DockStyle.Fill };
        Text = "Налаштування";
        Font = new Font("Segoe UI", 9);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        TopMost = true;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 614);
        BackColor = Color.FromArgb(24, 30, 41);
        ForeColor = Color.FromArgb(229, 237, 247);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 338));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var column = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        column.Controls.Add(Heading("Розмір і вигляд"));
        column.Controls.Add(Line("Масштаб, %", zoom));
        column.Controls.Add(Line("Відступ, px", gap));
        column.Controls.Add(Line("Відтінок фону, %", alpha));
        column.Controls.Add(Heading("Кольори"));

        for (int i = 0; i < ColorNames.Length; i++)
        {
            int index = i;
            hex[i] = new TextBox { Width = 99, MaxLength = 7, AccessibleName = ColorNames[i] + " HEX" };
            palette[i] = ButtonFor("Палітра…");
            palette[i].AccessibleName = "Палітра — " + ColorNames[i];
            var colorLine = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
            colorLine.Controls.Add(hex[i]);
            colorLine.Controls.Add(palette[i]);
            column.Controls.Add(Line(ColorNames[i], colorLine));
            hex[i].TextChanged += (sender, args) => AppearanceChanged();
            palette[i].Click += (sender, args) =>
            {
                using (var dialog = new ColorDialog { FullOpen = true, Color = StripSettings.ColorOf(CurrentColors()[index]) })
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        hex[index].Text = string.Format("#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B);
            };
        }

        try { startup.Checked = StartupRegistration.Enabled; }
        catch (Exception problem)
        {
            if (!StartupRegistration.IsAccessError(problem)) throw;
            startup.Enabled = false;
        }
        column.Controls.Add(Heading("Модулі смужки"));
        modules.AccessibleName = "Модулі смужки";
        var moduleLayout = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        moduleLayout.Controls.Add(modules);
        var arrows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        Button earlier = ButtonFor("↑"), later = ButtonFor("↓");
        earlier.AutoSize = later.AutoSize = false;
        earlier.Size = later.Size = new Size(34, 27);
        earlier.AccessibleName = "Вище";
        later.AccessibleName = "Нижче";
        earlier.Click += (sender, args) => MoveModule(-1);
        later.Click += (sender, args) => MoveModule(1);
        arrows.Controls.AddRange(new Control[] { earlier, later });
        moduleLayout.Controls.Add(arrows);
        column.Controls.Add(moduleLayout);
        column.Controls.Add(startup);
        column.Controls.Add(existingIsland);
        existingIsland.CheckedChanged += (sender, args) =>
        {
            if (loading) return;
            strip.Current.UseExistingIsland = existingIsland.Checked;
            strip.ApplySettings();
        };
        modules.ItemCheck += (sender, args) =>
        {
            if (loading) return;
            string id = (string)modules.Items[args.Index];
            var hidden = strip.Current.HiddenModules.Where(value => value != id);
            bool visible = args.NewValue == CheckState.Checked;
            if (id == "resources") strip.Current.ResourcesEnabled = true;
            strip.Current.HiddenModules = (visible ? hidden : hidden.Concat(new[] { id })).ToArray();
            strip.ApplySettings();
        };

        var previewArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(10, 0, 0, 9) };
        previewArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 195));
        previewArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        previewArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        previewArea.Controls.Add(Heading("Попередній перегляд трея"));
        previewArea.Controls.Add(preview);
        previewArea.Controls.Add(Heading("Пристрої в треї"));
        devices.AccessibleName = "Пристрої в треї";
        previewArea.Controls.Add(devices);
        var deviceActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        Button up = ButtonFor("↑"), down = ButtonFor("↓");
        up.AutoSize = down.AutoSize = false;
        up.Size = down.Size = new Size(34, 28);
        up.AccessibleName = "Вище";
        down.AccessibleName = "Нижче";
        up.Click += (sender, args) => MoveDevice(-1);
        down.Click += (sender, args) => MoveDevice(1);

        deviceActions.Controls.AddRange(new Control[] { up, down,
            new Label { Text = "Ліміт", AutoSize = true, Margin = new Padding(8, 8, 3, 0) }, maxDevices, displayMode });
        previewArea.Controls.Add(deviceActions);
        devices.ItemCheck += (sender, args) =>
        {
            if (loading) return;
            string name = (string)devices.Items[args.Index];
            var hidden = strip.Current.HiddenDevices.Where(n => n != name);
            strip.Current.HiddenDevices = (args.NewValue == CheckState.Checked ? hidden : hidden.Concat(new[] { name })).ToArray();
            strip.ApplySettings();
        };
        maxDevices.ValueChanged += (sender, args) => UpdateDeviceLayout();
        displayMode.SelectedIndexChanged += (sender, args) => UpdateDeviceLayout();
        language.Items.AddRange(Localize.Labels);
        var languageRow = Line("Мова", language);
        language.AccessibleName = "Мова";
        languageRow.Margin = new Padding(0, 0, 0, 8);
        root.Controls.Add(languageRow, 0, 0);
        root.SetColumnSpan(languageRow, 2);
        var general = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(6) };
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 338));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        general.Controls.Add(column, 0, 0);
        general.Controls.Add(previewArea, 1, 0);
        generalPage.Controls.Add(general);
        resourceEditor = new ResourceEditor(strip, ValidateColors) { Dock = DockStyle.Fill };
        resourcePage.Controls.Add(resourceEditor);
        BuildPositionPage();
        generalPage.BackColor = resourcePage.BackColor = positionPage.BackColor = BackColor;
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.AddRange(new[] { generalPage, resourcePage, positionPage });
        tabs.Selected += (sender, args) => { if (args.TabPage == positionPage) SyncPosition(); };
        root.Controls.Add(tabs, 0, 1);
        root.SetColumnSpan(tabs, 2);

        var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = ButtonFor("Скасувати");
        cancel.DialogResult = DialogResult.Cancel;
        var defaults = ButtonFor("Скинути до стандартних");
        footer.Controls.AddRange(new Control[] { save, cancel, defaults, error });
        root.Controls.Add(footer, 0, 2);
        root.SetColumnSpan(footer, 2);
        Controls.Add(root);
        Style(root);
        BindCaptions(root);
        modules.FormattingEnabled = true;
        modules.Format += (sender, args) =>
        {
            string id = (string)args.ListItem;
            args.Value = (Array.IndexOf(strip.Current.ModuleOrder, id) + 1) + " · " + Translate(StripSettings.ModuleLabel(id));
        };
        devices.FormattingEnabled = true;
        devices.Format += (sender, args) => args.Value = Translate((string)args.ListItem);
        language.SelectedIndexChanged += (sender, args) =>
        {
            if (loading || language.SelectedIndex < 0) return;
            strip.Current.Language = Localize.Languages[language.SelectedIndex];
            ApplyLanguage();
            ValidateColors();
            strip.ApplySettings();
        };
        save.BackColor = Color.FromArgb(38, 95, 171);
        AcceptButton = save;
        CancelButton = cancel;

        save.Click += (sender, args) => { if (strip.Commit(startup.Checked)) Close(); };
        cancel.Click += (sender, args) => Close();
        defaults.Click += (sender, args) => { strip.ResetSettings(); Sync(); };
        foreach (var control in new[] { zoom, gap, alpha })
        {
            control.ValueChanged += (sender, args) => AppearanceChanged();
            control.TextChanged += (sender, args) => AppearanceChanged();
        }
        Sync();
    }

    private void BuildPositionPage()
    {
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(12) };
        positionMode.AccessibleName = "Режим розташування";
        positionMonitor.AccessibleName = "Монітор";
        options.Controls.Add(Line("Режим розташування", positionMode));
        options.Controls.Add(Line("Монітор", positionMonitor));
        options.Controls.Add(Line("X", positionX));
        options.Controls.Add(Line("Y", positionY));
        var resetPosition = ButtonFor("Скинути позицію");
        options.Controls.Add(resetPosition);
        positionPage.Controls.Add(options);

        positionMode.SelectedIndexChanged += (sender, args) =>
        {
            if (loading || positionMode.SelectedIndex < 0) return;
            strip.Current.DockToTray = positionMode.SelectedIndex == 0;
            if (!strip.Current.IsDocked) SetManualPosition();
            SyncPosition();
            strip.ApplySettings();
        };
        positionMonitor.SelectedIndexChanged += (sender, args) =>
        {
            if (loading || positionMonitor.SelectedIndex < 0) return;
            loading = true;
            UpdatePositionLimits(monitors[positionMonitor.SelectedIndex]);
            loading = false;
            SetManualPosition();
        };
        positionX.ValueChanged += (sender, args) => SetManualPosition();
        positionY.ValueChanged += (sender, args) => SetManualPosition();
        positionX.TextChanged += (sender, args) => SetManualPosition();
        positionY.TextChanged += (sender, args) => SetManualPosition();
        resetPosition.Click += (sender, args) =>
        {
            strip.Current.DockToTray = true;
            strip.Current.StripX = strip.Current.StripY = null;
            strip.Current.StripMonitor = "";
            strip.ApplySettings();
            SyncPosition();
        };
    }

    private void SetManualPosition()
    {
        if (loading || strip.Current.IsDocked || positionMonitor.SelectedIndex < 0) return;
        var settings = strip.Current;
        string monitor = monitors[positionMonitor.SelectedIndex].DeviceName;
        int x = ReadNumber(positionX, (int)positionX.Value);
        int y = ReadNumber(positionY, (int)positionY.Value);
        if (settings.StripMonitor == monitor && settings.StripX == x && settings.StripY == y) return;
        settings.StripMonitor = monitor;
        settings.StripX = x;
        settings.StripY = y;
        strip.ApplySettings();
    }

    private void UpdatePositionLimits(Screen screen)
    {
        positionX.Maximum = screen.Bounds.Width - 1;
        positionY.Maximum = screen.Bounds.Height - 1;
    }

    internal void SyncPosition()
    {
        bool wasLoading = loading;
        loading = true;
        var settings = strip.Current;
        var screen = strip.ModuleScreen(settings.StripMonitor);
        monitors = Screen.AllScreens;
        positionMonitor.Items.Clear();
        for (int i = 0; i < monitors.Length; i++)
        {
            var bounds = monitors[i].Bounds;
            positionMonitor.Items.Add(string.Format("{0} · {1} × {2}", i + 1, bounds.Width, bounds.Height));
        }
        positionMonitor.SelectedIndex = Array.FindIndex(monitors, monitor => monitor.DeviceName == screen.DeviceName);
        positionMode.SelectedIndex = settings.IsDocked ? 0 : 1;
        positionMonitor.Enabled = positionX.Enabled = positionY.Enabled = !settings.IsDocked;
        UpdatePositionLimits(screen);
        Rectangle visible = strip.ModuleBounds;
        Point fallback = !visible.IsEmpty && screen.Bounds.Contains(visible.Location)
            ? new Point(visible.Left - screen.Bounds.Left, visible.Top - screen.Bounds.Top)
            : new Point(Math.Max(0, screen.Bounds.Width - 260), Math.Max(0, screen.WorkingArea.Bottom - screen.Bounds.Top - 40));
        positionX.Value = StripSettings.Clamp(settings.StripX ?? fallback.X, 0, (int)positionX.Maximum);
        positionY.Value = StripSettings.Clamp(settings.StripY ?? fallback.Y, 0, (int)positionY.Maximum);
        loading = wasLoading;
    }

    internal void Sync()
    {
        loading = true;
        var settings = strip.Current;
        existingIsland.Checked = settings.UseExistingIsland;
        zoom.Value = settings.Zoom;
        gap.Value = settings.Gap;
        alpha.Value = settings.Alpha;
        maxDevices.Value = settings.MaxDevices;
        language.SelectedIndex = Array.IndexOf(Localize.Languages, settings.Language);
        ApplyLanguage();
        string[] colors = CurrentColors();
        for (int i = 0; i < hex.Length; i++) hex[i].Text = colors[i];
        loading = false;
        ValidateColors();
        SyncReadings();
    }

    internal void SyncReadings()
    {
        if (loading) return;
        RebuildDevices(false);
        preview.Invalidate();
        resourceEditor.RefreshPreview();
    }

    private string Translate(string text)
    {
        return Localize.Text(strip.Current.Language, text);
    }

    private void BindCaptions(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            if (control is Label || control is ButtonBase)
            {
                string source = control.Text;
                captions.Add(code => control.Text = Localize.Text(code, source));
            }
            string accessible = control.AccessibleName;
            if (!string.IsNullOrEmpty(accessible))
                captions.Add(code => control.AccessibleName = Localize.Text(code, accessible));
            BindCaptions(control);
        }
    }

    private void ApplyLanguage()
    {
        bool previous = loading;
        loading = true;
        string code = strip.Current.Language;
        Text = Translate("Налаштування");
        generalPage.Text = Translate("Загальні");
        resourcePage.Text = Translate("Ресурси ПК");
        positionPage.Text = Translate("Позиція");
        foreach (var caption in captions) caption(code);
        positionMode.Items.Clear();
        positionMode.Items.AddRange(new object[] { Translate("Ліворуч від системного трея"), Translate("Ручна X/Y") });
        SyncPosition();
        resourceEditor.Sync();
        for (int i = 0; i < hex.Length; i++)
        {
            string name = Translate(ColorNames[i]);
            hex[i].AccessibleName = name + " HEX";
            palette[i].AccessibleName = Translate("Палітра") + " — " + name;
        }
        displayMode.Items.Clear();
        displayMode.Items.AddRange(new object[] { Translate("Іконка + %"), Translate("Тільки іконка"), Translate("Тільки %") });
        displayMode.SelectedIndex = strip.Current.DisplayMode;
        RebuildModules();
        RebuildDevices(true);
        loading = previous;
    }

    private void RebuildModules()
    {
        bool old = loading;
        loading = true;
        string selected = modules.SelectedItem as string;
        modules.BeginUpdate();
        modules.Items.Clear();
        foreach (string id in strip.Current.ModuleOrder) modules.Items.Add(id, strip.Current.ModuleVisible(id));
        if (selected != null) modules.SelectedItem = selected;
        modules.EndUpdate();
        loading = old;
    }

    private void MoveModule(int delta)
    {
        int index = modules.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= modules.Items.Count) return;
        var order = (string[])strip.Current.ModuleOrder.Clone();
        string swapped = order[target];
        order[target] = order[index];
        order[index] = swapped;
        strip.Current.ModuleOrder = order;
        RebuildModules();
        strip.ApplySettings();
    }

    private void RebuildDevices(bool force)
    {
        var current = strip.Current;
        string[] names = current.Ordered(strip.AllReadings).Select(row => row.Name).Distinct().ToArray();
        if (!force && devices.Items.Cast<string>().SequenceEqual(names)) return;
        string selected = devices.SelectedItem as string;
        bool previous = loading;
        loading = true;
        devices.BeginUpdate();
        devices.Items.Clear();
        foreach (string name in names) devices.Items.Add(name, !current.HiddenDevices.Contains(name));
        if (selected != null && names.Contains(selected)) devices.SelectedItem = selected;
        devices.EndUpdate();
        loading = previous;
    }

    private void MoveDevice(int delta)
    {
        int index = devices.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= devices.Items.Count) return;
        var order = devices.Items.Cast<string>().ToList();
        string name = order[index];
        order.RemoveAt(index);
        order.Insert(target, name);
        strip.Current.DeviceOrder = order.Concat(strip.Current.DeviceOrder.Except(order)).ToArray();
        strip.ApplySettings();
    }

    private void UpdateDeviceLayout()
    {
        if (loading || displayMode.SelectedIndex < 0) return;
        strip.Current.MaxDevices = (int)maxDevices.Value;
        strip.Current.DisplayMode = displayMode.SelectedIndex;
        strip.ApplySettings();
    }

    private static void Style(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            control.ForeColor = Color.FromArgb(229, 237, 247);
            var button = control as Button;
            if (button != null)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Color.FromArgb(64, 78, 98);
                button.BackColor = Color.FromArgb(39, 51, 68);
            }
            else if (control is TextBox || control is ComboBox || control is NumericUpDown || control is CheckedListBox)
                control.BackColor = Color.FromArgb(35, 45, 61);
            Style(control);
        }
    }

    private string[] CurrentColors()
    {
        var s = strip.Current;
        return new[] { s.Background, s.Text, s.Icons, s.Low };
    }

    private void ValidateColors()
    {
        bool valid = resourceEditor.ValidateColor();
        for (int i = 0; i < hex.Length; i++)
        {
            Color color;
            bool correct = StripSettings.TryColor(hex[i].Text, out color);
            valid &= correct;
            hex[i].BackColor = correct ? Color.FromArgb(35, 45, 61) : Color.FromArgb(99, 40, 50);
        }
        error.Text = valid ? "" : Translate("Формат #RRGGBB");
        save.Enabled = valid;
    }

    private void AppearanceChanged()
    {
        if (loading) return;
        var s = strip.Current;
        s.Zoom = ReadNumber(zoom, s.Zoom);
        s.Gap = ReadNumber(gap, s.Gap);
        s.Alpha = ReadNumber(alpha, s.Alpha);
        string[] colors = CurrentColors();
        for (int i = 0; i < hex.Length; i++)
        {
            Color color;
            if (StripSettings.TryColor(hex[i].Text, out color)) colors[i] = hex[i].Text.ToUpperInvariant();
        }
        s.Background = colors[0];
        s.Text = colors[1];
        s.Icons = colors[2];
        s.Low = colors[3];
        ValidateColors();
        strip.ApplySettings();
        SyncReadings();
    }

    private static int ReadNumber(NumericUpDown field, int previous)
    {
        int value;
        return int.TryParse(field.Text, out value) && value >= field.Minimum && value <= field.Maximum ? value : previous;
    }

    private static Button ButtonFor(string text) { return new Button { Text = text, AutoSize = true }; }
    private static NumericUpDown Number(string name, int min, int max)
    {
        return new NumericUpDown { AccessibleName = name, Minimum = min, Maximum = max, Width = 73 };
    }

    private static Label Heading(string text)
    {
        return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(0, 6, 0, 3) };
    }

    private static Control Line(string text, Control control)
    {
        var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        line.Controls.Add(new Label { Text = text, Width = 128, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 1, 3, 0) });
        line.Controls.Add(control);
        return line;
    }

    private sealed class Preview : Control
    {
        private readonly Strip owner;
        internal Preview(Strip strip) { owner = strip; DoubleBuffered = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var settings = owner.Current;
            var rows = owner.Readings;
            Color body = StripSettings.PopupColor(settings.Background, settings.Alpha);
            var canvas = ClientRectangle;
            if (canvas.Width < 2 || canvas.Height < 2) return;
            using (var backdrop = new LinearGradientBrush(canvas, Color.FromArgb(35, 58, 94), Color.FromArgb(21, 29, 46), 65f))
            using (var panel = new SolidBrush(body))
            using (var bar = new SolidBrush(Color.FromArgb(19, 23, 33)))
            using (var border = new Pen(Color.FromArgb(105, 143, 165, 190)))
            using (var title = new Font("Segoe UI", 10, FontStyle.Bold))
            using (var label = new Font("Segoe UI", 9))
            {
                e.Graphics.FillRectangle(backdrop, canvas);
                var tray = new Rectangle(0, canvas.Bottom - 34, canvas.Width, 34);
                e.Graphics.FillRectangle(bar, tray);
                int width = ModuleStrip.PreviewWidth(settings, tray.Width - 66, 32);
                ModuleStrip.Draw(e.Graphics, new Rectangle(tray.Right - width - 51, tray.Top + 1, width, 32), owner);
                TextRenderer.DrawText(e.Graphics, "22:41", label, new Point(tray.Right - 48, tray.Top + 8), Color.WhiteSmoke);
                int count = Math.Min(rows.Count, 3);
                var popup = new Rectangle(16, 10, canvas.Width - 32, 37 + Math.Max(1, count) * 29);
                e.Graphics.FillRectangle(panel, popup);
                e.Graphics.DrawRectangle(border, popup);
                TextRenderer.DrawText(e.Graphics, "Charge Strip", title, new Point(popup.X + 12, popup.Y + 8), StripSettings.ColorOf(settings.Text));
                for (int i = 0; i < count; i++)
                {
                    var row = rows[i];
                    string value = settings.DisplayMode == 1 ? "" : row.Level;
                    var rowColor = StripSettings.ColorOf(row.Percent <= 25 ? settings.Low : settings.Icons);
                    int y = popup.Y + 35 + i * 28;
                    int left = popup.X + 12;
                    if (settings.DisplayMode != 2)
                    {
                        using (var deviceFont = new Font("Segoe MDL2 Assets", 11))
                            TextRenderer.DrawText(e.Graphics, row.Icon, deviceFont, new Point(left, y), rowColor);
                        left += 25;
                    }
                    TextRenderer.DrawText(e.Graphics, owner.Translate(row.Name), label,
                        new Rectangle(left, y, popup.Right - left - 52, 20), StripSettings.ColorOf(settings.Text),
                        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(e.Graphics, value, label, new Rectangle(popup.Right - 54, y, 44, 20), rowColor, TextFormatFlags.Right);
                }
            }
        }
    }
}
