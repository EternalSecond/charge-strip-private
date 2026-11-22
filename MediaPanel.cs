using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
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
            if (Artwork != null) { Artwork.Dispose(); Artwork = null; }
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
                decodedArtwork = next.Artwork;
                Bitmap previous = Artwork;
                Artwork = null;
                if (decodedArtwork != null)
                {
                    using (var stream = new MemoryStream(decodedArtwork))
                    using (var image = Image.FromStream(stream))
                        Artwork = new Bitmap(image);
                }
                if (panel != null) panel.SetArtwork(Artwork);
                if (previous != null) previous.Dispose();
            }
            if (Changed != null) Changed();
        }
        if (panel != null) panel.UpdateSnapshot(snapshot);
    }

    internal void Open(Rectangle anchor)
    {
        if (panel != null) { Close(); return; }
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
        if (Artwork != null) Artwork.Dispose();
        if (service != null) service.Dispose();
    }
}

internal sealed class MediaCard : Form
{
    private readonly StripSettings settings;
    private readonly Rectangle anchor;
    private readonly Action<string> select;
    private readonly Action<string, string, TimeSpan> command;
    private readonly PictureBox artwork = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill };
    private readonly ComboBox sources = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label title = new Label { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label artist = new Label { AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly Label timestamp = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
    private readonly TrackBar seek = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Dock = DockStyle.Fill };
    private readonly Button previous, playPause, next;
    private readonly Font titleFont = new Font("Segoe UI", 13, FontStyle.Bold);
    private readonly Font normalFont = new Font("Segoe UI", 9);
    private MediaSnapshot snapshot = new MediaSnapshot();
    private bool updating, scrubbing, active;
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
        BackColor = StripSettings.PopupColor(settings.Background, settings.Alpha);
        ForeColor = StripSettings.ColorOf(settings.Text);
        Font = normalFont;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(14) };
        foreach (int height in new[] { 28, 0, 32, 22, 24, 20, 39 })
            layout.RowStyles.Add(height == 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        header.Controls.Add(new Label { Text = "Charge Strip · " + Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 0, 0);
        header.Controls.Add(sources, 1, 0);
        sources.AccessibleName = L("Джерело медіа");
        sources.SelectedIndexChanged += (sender, args) =>
        {
            if (!updating && sources.SelectedIndex >= 0) select(sourceIds[sources.SelectedIndex]);
        };
        layout.Controls.Add(header, 0, 0);
        artwork.BackColor = Color.FromArgb(22, 25, 33);
        artwork.Margin = new Padding(0, 5, 0, 2);
        layout.Controls.Add(artwork, 0, 1);
        title.Font = titleFont;
        layout.Controls.Add(title, 0, 2);
        layout.Controls.Add(artist, 0, 3);
        seek.AccessibleName = L("Позиція відтворення");
        seek.MouseDown += (sender, args) => { if (seek.Enabled) scrubbing = true; };
        seek.MouseUp += (sender, args) =>
        {
            if (!scrubbing) return;
            scrubbing = false;
            if (seek.Enabled)
            {
                int width = Math.Max(1, seek.ClientSize.Width - 16);
                seek.Value = StripSettings.Clamp((args.X - 8) * 1000 / width, 0, 1000);
                SendSeek();
            }
        };
        seek.KeyUp += (sender, args) =>
        {
            if (args.KeyCode == Keys.Left || args.KeyCode == Keys.Right || args.KeyCode == Keys.Home || args.KeyCode == Keys.End)
                SendSeek();
        };
        layout.Controls.Add(seek, 0, 4);
        layout.Controls.Add(timestamp, 0, 5);

        var controls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        for (int i = 0; i < 3; i++) controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        previous = MakeButton("‹‹", L("Попередній"), "previous");
        playPause = MakeButton("▶", L("Відтворити"), "play");
        next = MakeButton("››", L("Наступний"), "next");
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
            new Size((int)(326 * scale), (int)(405 * scale)));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Place(DeviceDpi);
        Native.RoundCorners(Handle);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); active = true; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (active) Close(); }
    protected override bool ProcessCmdKey(ref Message message, Keys key)
    {
        if (key == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, key);
    }

    private string L(string text) { return Localize.Text(settings.Language, text); }

    private Button MakeButton(string caption, string name, string action)
    {
        var button = new Button { Text = caption, AccessibleName = name, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            ForeColor = ForeColor, BackColor = Color.FromArgb(38, 38, 48) };
        button.FlatAppearance.BorderColor = StripSettings.ColorOf(settings.Icons);
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
        playPause.Text = value.Playing ? "Ⅱ" : "▶";
        playPause.AccessibleName = L(value.Playing ? "Пауза" : "Відтворити");

        TimeSpan duration = value.End - value.Start;
        TimeSpan elapsed = value.CurrentPosition - value.Start;
        seek.Enabled = value.CanSeek && duration > TimeSpan.Zero;
        if (!scrubbing) seek.Value = duration > TimeSpan.Zero
            ? StripSettings.Clamp((int)(1000 * elapsed.TotalSeconds / duration.TotalSeconds), 0, 1000) : 0;
        timestamp.Text = value.Source == null ? "" : Format(elapsed) + (duration > TimeSpan.Zero ? " / " + Format(duration) : "");
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
        }
        base.Dispose(disposing);
    }
}
