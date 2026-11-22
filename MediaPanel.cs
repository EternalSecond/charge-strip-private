using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class MediaModule : IDisposable
{
    private readonly Func<StripSettings> preferences;
    private readonly Action dismissOthers;
    private readonly Timer timer = new Timer { Interval = 250 };
    private MediaService service;
    private MediaCard panel;
    private MediaSnapshot snapshot = new MediaSnapshot();
    private byte[] decodedArtwork;
    internal MediaSnapshot Snapshot { get { return snapshot; } }
    internal Bitmap Artwork { get; private set; }
    internal event Action Changed;

    internal MediaModule(Func<StripSettings> preferences, Action dismissOthers)
    {
        this.preferences = preferences;
        this.dismissOthers = dismissOthers;
        timer.Tick += (sender, args) => Update();
    }

    internal void Apply(bool enabled)
    {
        if (!enabled)
        {
            Close();
            timer.Stop();
            if (service != null) { service.Dispose(); service = null; }
            SetArtwork(null);
            snapshot = new MediaSnapshot();
            decodedArtwork = null;
            return;
        }
        if (service == null)
        {
            service = new MediaService();
            timer.Start();
        }
        Update();
    }

    private void Update()
    {
        if (service == null) return;
        var next = service.Current;
        if (!ReferenceEquals(snapshot, next))
        {
            snapshot = next;
            if (!ReferenceEquals(decodedArtwork, next.Artwork))
            {
                bool changedTrack = decodedArtwork != null && next.Artwork == null;
                decodedArtwork = next.Artwork;
                if (changedTrack) SetArtwork(null);
                if (decodedArtwork != null) PrepareArtwork(decodedArtwork, service);
            }
            if (Changed != null) Changed();
        }
        if (panel != null) panel.UpdateSnapshot(snapshot);
    }

    private async void PrepareArtwork(byte[] data, MediaService activeService)
    {
        Bitmap image = await Task.Run(() =>
        {
            using (var stream = new MemoryStream(data))
            using (var source = Image.FromStream(stream))
                return new Bitmap(source);
        });
        if (!ReferenceEquals(service, activeService) || !ReferenceEquals(decodedArtwork, data))
        {
            image.Dispose();
            return;
        }
        SetArtwork(image);
        if (Changed != null) Changed();
    }

    private void SetArtwork(Bitmap image)
    {
        Bitmap previous = Artwork;
        Artwork = image;
        if (panel != null) panel.SetArtwork(image);
        if (previous != null) previous.Dispose();
    }

    internal void Open(Rectangle anchor)
    {
        if (panel != null) { Close(); return; }
        if (service == null) return;
        dismissOthers();
        if (anchor.IsEmpty) anchor = new Rectangle(Cursor.Position, new Size(1, 1));
        panel = new MediaCard(preferences(), anchor, service.Select, service.Command);
        panel.SetArtwork(Artwork);
        panel.UpdateSnapshot(snapshot);
        panel.FormClosed += (sender, args) => { if (ReferenceEquals(panel, sender)) panel = null; };
        panel.Show();
        if (panel != null) panel.Activate();
    }

    internal void Close()
    {
        if (panel == null) return;
        var previous = panel;
        panel = null;
        previous.Close();
    }

    public void Dispose()
    {
        timer.Dispose();
        Close();
        SetArtwork(null);
        if (service != null) { service.Dispose(); service = null; }
    }
}

internal sealed class MediaCard : Form
{
    private readonly StripSettings settings;
    private readonly Rectangle anchor;
    private readonly Action<string> select;
    private readonly Action<string, string, TimeSpan> command;
    private readonly PictureBox artwork = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
    private readonly ComboBox sources = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly Label title = new Label { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label artist = new Label { AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly Label elapsedTime = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label totalTime = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
    private readonly MediaProgress seek = new MediaProgress { Dock = DockStyle.Fill };
    private readonly Button previous, playPause, next;
    private readonly Font titleFont, normalFont, controlFont;
    private readonly Color accent, outline;
    private MediaSnapshot snapshot = new MediaSnapshot();
    private bool updating, active;
    private string[] sourceIds = new string[0];

    internal MediaCard(StripSettings settings, Rectangle anchor, Action<string> select, Action<string, string, TimeSpan> command)
    {
        this.settings = settings;
        this.anchor = anchor;
        this.select = select;
        this.command = command;
        Text = L("Медіа");
        AccessibleName = Text;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = StripAppearance.Surface(settings);
        ForeColor = StripSettings.ColorOf(settings.Text);
        accent = StripAppearance.Mix(StripSettings.ColorOf(settings.Icons), Color.FromArgb(202, 139, 159), .36);
        outline = StripAppearance.Mix(BackColor, accent, .31);
        Opacity = .92 + settings.Alpha * .0007;
        float zoom = settings.Zoom / 100f;
        normalFont = new Font("Segoe UI", 9 * zoom);
        titleFont = new Font("Segoe UI", 13 * zoom, FontStyle.Bold);
        controlFont = new Font("Segoe MDL2 Assets", 12 * zoom);
        Font = normalFont;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(16), BackColor = Color.Transparent };
        foreach (int height in new[] { 30, 0, 40, 20, 24, 20, 42 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, (int)(height * zoom)));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        header.Controls.Add(new Label { Text = "Charge Strip · " + Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, ForeColor = accent }, 0, 0);
        header.Controls.Add(sources, 1, 0);
        sources.AccessibleName = L("Джерело медіа");
        sources.BackColor = StripAppearance.Mix(BackColor, accent, .11);
        sources.ForeColor = ForeColor;
        sources.DrawItem += (sender, args) =>
        {
            bool selected = (args.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(selected ? StripAppearance.Mix(BackColor, accent, .24) : sources.BackColor))
                args.Graphics.FillRectangle(fill, args.Bounds);
            if (args.Index >= 0)
                TextRenderer.DrawText(args.Graphics, sources.GetItemText(sources.Items[args.Index]), normalFont, args.Bounds, ForeColor,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        sources.SelectedIndexChanged += (sender, args) =>
        {
            if (!updating && sources.SelectedIndex >= 0) select(sourceIds[sources.SelectedIndex]);
        };
        layout.Controls.Add(header, 0, 0);
        artwork.BackColor = StripAppearance.Mix(BackColor, Color.Black, .25);
        artwork.Margin = new Padding(0, 4, 0, 5);
        artwork.Resize += (sender, args) => ApplyRoundedRegion(artwork, 9);
        layout.Controls.Add(artwork, 0, 1);
        title.Font = titleFont;
        artist.ForeColor = StripAppearance.Mix(BackColor, ForeColor, .77);
        layout.Controls.Add(title, 0, 2);
        layout.Controls.Add(artist, 0, 3);
        seek.AccessibleName = L("Позиція відтворення");
        seek.BackColor = BackColor;
        seek.Accent = accent;
        seek.Rail = outline;
        seek.ValueCommitted += (sender, args) => SendSeek();
        layout.Controls.Add(seek, 0, 4);
        var time = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        time.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        time.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        elapsedTime.Margin = totalTime.Margin = new Padding(4, 0, 4, 0);
        elapsedTime.ForeColor = ForeColor;
        totalTime.ForeColor = artist.ForeColor;
        time.Controls.Add(elapsedTime, 0, 0);
        time.Controls.Add(totalTime, 1, 0);
        layout.Controls.Add(time, 0, 5);

        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        for (int i = 0; i < 3; i++) controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        previous = MakeButton("\uE892", L("Попередній"), "previous");
        playPause = MakeButton("\uE768", L("Відтворити"), "play");
        next = MakeButton("\uE893", L("Наступний"), "next");
        controls.Controls.Add(previous, 0, 0);
        controls.Controls.Add(playPause, 1, 0);
        controls.Controls.Add(next, 2, 0);
        layout.Controls.Add(controls, 0, 6);
        Controls.Add(layout);
        Place(96);
    }

    internal static Rectangle PopupBounds(Rectangle anchor, Rectangle work, Size requested)
    {
        int width = Math.Min(work.Width, requested.Width);
        int height = Math.Min(work.Height, requested.Height);
        return new Rectangle(StripSettings.Clamp(anchor.Right - width, work.Left, work.Right - width),
            StripSettings.Clamp(anchor.Top - height - 7, work.Top, work.Bottom - height), width, height);
    }

    private void Place(int dpi)
    {
        double scale = settings.Zoom / 100.0 * dpi / 96.0;
        Bounds = PopupBounds(anchor, Screen.FromRectangle(anchor).WorkingArea,
            new Size((int)(350 * scale), (int)(420 * scale)));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Place(DeviceDpi);
        Native.RoundCorners(Handle);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW on the flyout.
            return parameters;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (settings != null) ApplyRoundedRegion(this, StripAppearance.CornerRadius);
    }

    private static void ApplyRoundedRegion(Control control, int radius)
    {
        if (control.Width < radius * 2 || control.Height < radius * 2) return;
        using (var path = StripAppearance.Rounded(new Rectangle(0, 0, control.Width, control.Height), radius))
        {
            Region previous = control.Region;
            control.Region = new Region(path);
            if (previous != null) previous.Dispose();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var line = new Pen(outline))
        using (var edge = StripAppearance.Rounded(new Rectangle(1, 1, Width - 2, Height - 2), StripAppearance.CornerRadius))
            e.Graphics.DrawPath(line, edge);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (playPause.Enabled) ActiveControl = playPause;
        active = true;
    }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (active) Close(); }
    protected override bool ProcessCmdKey(ref Message message, Keys key)
    {
        if (key == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, key);
    }

    private string L(string text) { return Localize.Text(settings.Language, text); }

    private Button MakeButton(string caption, string name, string action)
    {
        bool primary = action == "play";
        var button = new Button { Text = caption, AccessibleName = name, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            Font = controlFont, ForeColor = primary ? ForeColor : accent,
            BackColor = StripAppearance.Mix(BackColor, accent, primary ? .27 : .12), UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = StripAppearance.Mix(BackColor, accent, .36);
        button.SizeChanged += (sender, args) => ApplyRoundedRegion(button, 8);
        button.Click += (sender, args) => command(action, snapshot.Source, TimeSpan.Zero);
        return button;
    }

    internal void SetArtwork(Image image) { artwork.Image = image; }

    internal void UpdateSnapshot(MediaSnapshot value)
    {
        snapshot = value;
        title.Text = string.IsNullOrWhiteSpace(value.Title) ? L("Немає активного медіа") : value.Title;
        artist.Text = string.IsNullOrWhiteSpace(value.Artist) ? MediaSnapshot.SourceLabel(value.Source) : value.Artist;
        updating = true;
        if (!sourceIds.SequenceEqual(value.Sources))
        {
            sourceIds = value.Sources;
            sources.Items.Clear();
            sources.Items.AddRange(sourceIds.Select(id => (object)MediaSnapshot.SourceLabel(id)).ToArray());
        }
        if (!sources.DroppedDown) sources.SelectedIndex = Array.IndexOf(sourceIds, value.Source);
        sources.Enabled = sourceIds.Length > 1;
        updating = false;
        previous.Enabled = value.CanPrevious;
        next.Enabled = value.CanNext;
        playPause.Enabled = value.CanPlayPause;
        playPause.Text = value.Playing ? "\uE769" : "\uE768";
        playPause.AccessibleName = L(value.Playing ? "Пауза" : "Відтворити");

        TimeSpan duration = value.End - value.Start;
        TimeSpan elapsed = value.CurrentPosition - value.Start;
        seek.Enabled = value.CanSeek && duration > TimeSpan.Zero;
        if (!seek.Scrubbing) seek.Value = duration > TimeSpan.Zero
            ? StripSettings.Clamp((int)(1000 * elapsed.TotalSeconds / duration.TotalSeconds), 0, 1000) : 0;
        elapsedTime.Text = value.Source == null ? "" : Format(elapsed);
        totalTime.Text = duration > TimeSpan.Zero ? Format(duration) : "";
    }

    private static string Format(TimeSpan value)
    {
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void SendSeek()
    {
        if (!seek.Enabled || !snapshot.CanSeek || snapshot.End <= snapshot.Start) return;
        command("seek", snapshot.Source, snapshot.Start + TimeSpan.FromTicks((snapshot.End - snapshot.Start).Ticks * seek.Value / 1000));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            artwork.Image = null;
            titleFont.Dispose();
            normalFont.Dispose();
            controlFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class MediaProgress : Control
{
    private const int Inset = 8, Maximum = 1000;
    private int value;
    internal Color Accent, Rail;
    internal bool Scrubbing { get; private set; }
    internal event EventHandler ValueCommitted;
    internal int Value
    {
        get { return value; }
        set
        {
            int position = StripSettings.Clamp(value, 0, Maximum);
            if (this.value == position) return;
            this.value = position;
            Invalidate();
        }
    }

    internal MediaProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float center = Height / 2f;
        float progress = Inset + TrackWidth * Value / (float)Maximum;
        using (var rail = new Pen(Rail, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var fill = new Pen(Accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var thumb = new SolidBrush(Accent))
        {
            e.Graphics.DrawLine(rail, Inset, center, Inset + TrackWidth, center);
            if (Value > 0) e.Graphics.DrawLine(fill, Inset, center, progress, center);
            if (Enabled) e.Graphics.FillEllipse(thumb, progress - 4, center - 4, 8, 8);
        }
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }

    private int TrackWidth { get { return Math.Max(1, Width - 2 * Inset); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        Focus();
        Capture = Scrubbing = true;
        Value = (e.X - Inset) * Maximum / TrackWidth;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Scrubbing) Value = (e.X - Inset) * Maximum / TrackWidth;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !Scrubbing) return;
        Value = (e.X - Inset) * Maximum / TrackWidth;
        Scrubbing = Capture = false;
        Commit();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) Scrubbing = false;
        base.OnMouseCaptureChanged(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Home || keyData == Keys.End || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left: Value -= 20; break;
            case Keys.Right: Value += 20; break;
            case Keys.Home: Value = 0; break;
            case Keys.End: Value = Maximum; break;
            default: return;
        }
        e.Handled = true;
        Commit();
    }

    private void Commit()
    {
        if (Enabled && ValueCommitted != null) ValueCommitted(this, EventArgs.Empty);
    }
}
