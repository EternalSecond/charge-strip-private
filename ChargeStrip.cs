using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool launchEditor = args.Contains("--settings");
        bool first;
        using (var request = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ChargeStrip.OpenSettings"))
        using (var instance = new Mutex(true, @"Local\ChargeStrip", out first))
        {
            if (!first)
            {
                if (launchEditor)
                {
                    request.Set();
                }
                return;
            }
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var instanceContext = new Strip(request, launchEditor))
                Application.Run(instanceContext);
        }
    }
}

internal sealed class Strip : ApplicationContext
{
    private static readonly Color Background = Color.FromArgb(31, 35, 43);
    private static readonly Color TextColor = Color.FromArgb(240, 243, 247);
    private static readonly Color Muted = Color.FromArgb(174, 183, 196);
    private readonly ContextMenuStrip details = new ContextMenuStrip();
    private readonly ContextMenuStrip stripMenu = new ContextMenuStrip();
    private readonly ResourcesModule resources;
    private readonly MediaModule media;
    private readonly ModuleStrip surface;
    private readonly Font popupHeaderFont = new Font("Segoe UI", 10, FontStyle.Bold);
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 12000 };
    private readonly System.Windows.Forms.Timer inputTimer = new System.Windows.Forms.Timer { Interval = 150 };
    private string menuLanguage;
    private List<BatteryReading> rows = new List<BatteryReading>();
    private DateTime? refreshed;
    private StripSettings saved, draft;
    private SettingsWindow editor;
    private bool polling, disposed;

    internal StripSettings Current { get { return draft ?? saved; } }
    internal IList<BatteryReading> AllReadings { get { return rows; } }
    internal List<BatteryReading> Readings { get { return Current.Selected(rows).Take(Current.MaxDevices).ToList(); } }
    internal ResourceReading ResourceReading { get { return resources.Reading; } }

    public Strip(EventWaitHandle openRequest, bool launchEditor)
    {
        saved = StripSettings.Load() ?? new StripSettings();
        resources = new ResourcesModule(() => Current, () => { details.Close(); if (media != null) media.Close(); });
        media = new MediaModule(() => Current, () => { details.Close(); resources.Close(); });
        surface = new ModuleStrip(this);
        media.Changed += () => { surface.UpdateDrawing(); if (editor != null) editor.SyncReadings(); };
        resources.Changed += () => { surface.UpdateDrawing(); if (editor != null) editor.SyncReadings(); };
        details.ShowImageMargin = false;
        details.Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false };
        details.ForeColor = TextColor;
        details.Closing += (sender, args) =>
        {
            if ((args.CloseReason == ToolStripDropDownCloseReason.AppClicked
                || (args.CloseReason == ToolStripDropDownCloseReason.AppFocusChange
                    && (Control.MouseButtons & MouseButtons.Left) != 0))
                && surface.VisibleBounds.Contains(Cursor.Position))
                args.Cancel = true;
        };
        details.Opened += (sender, args) => Native.RoundCorners(details.Handle);
        stripMenu.Items.Add("Налаштування", null, (sender, args) => OpenSettings());
        stripMenu.Items.Add("Оновити", null, async (sender, args) => await RefreshReadings());
        stripMenu.Items.Add("Вийти", null, (sender, args) => ExitThread());

        ApplySettings();
        bool initialRead = true;
        inputTimer.Tick += async (sender, args) =>
        {
            if (launchEditor || openRequest.WaitOne(0))
            {
                launchEditor = false;
                OpenSettings();
            }
            if (initialRead)
            {
                initialRead = false;
                await RefreshReadings();
            }
        };
        timer.Tick += async (sender, args) => await RefreshReadings();
        inputTimer.Start();
        timer.Start();
    }

    internal string Translate(string text) { return Localize.Text(Current.Language, text); }

    internal void ApplySettings()
    {
        if (disposed) return;
        if (menuLanguage != Current.Language)
        {
            menuLanguage = Current.Language;
            stripMenu.Items[0].Text = Translate("Налаштування");
            stripMenu.Items[1].Text = Translate("Оновити");
            stripMenu.Items[2].Text = Translate("Вийти");
        }
        if (!Current.ModuleVisible("batteries")) details.Close();
        resources.Apply();
        media.Apply(Current.ModuleVisible("media"));
        FillDetails();
        SyncModuleSurface();
        if (editor != null) editor.SyncReadings();
    }

    internal MediaSnapshot MediaSnapshot { get { return media.Snapshot; } }
    internal Bitmap MediaArtwork { get { return media.Artwork; } }
    internal void SyncModuleSurface()
    {
        if (disposed) return;
        Rectangle old = surface.VisibleBounds;
        surface.Arrange();
        if (old != surface.VisibleBounds)
        {
            details.Close();
            stripMenu.Close();
            resources.Close();
            media.Close();
        }
    }

    internal string ModuleHint(string id)
    {
        if (id == "batteries")
        {
            var batteries = Readings.Take(2).ToArray();
            if (batteries.Length != 0)
                return string.Join("\n", batteries.Select(row => Translate(row.Name) + " · " + row.Level));
        }
        if (id == "media" && !string.IsNullOrWhiteSpace(media.Snapshot.Title))
            return media.Snapshot.Title + (string.IsNullOrWhiteSpace(media.Snapshot.Artist) ? "" : " · " + media.Snapshot.Artist);
        if (id == "resources")
        {
            string readings = resources.Reading.Describe(Current.VisibleMetrics(true), Current.Language);
            return string.IsNullOrEmpty(readings) ? Translate("Ресурси ПК") : readings;
        }
        return Translate(StripSettings.ModuleLabel(id));
    }

    internal void OpenModule(string id, Rectangle anchor)
    {
        if (id == "media") media.Open(anchor);
        else if (id == "resources") resources.Open(anchor);
        else OpenDetails(new Point(anchor.Right, anchor.Top - 5));
    }

    internal Rectangle ModuleBounds { get { return surface.VisibleBounds; } }
    internal Screen ModuleScreen(string monitor) { return surface.PositionScreen(monitor); }

    internal void SaveModulePosition(string monitor, int x, int y)
    {
        var target = draft ?? saved.Copy();
        target.DockToTray = false;
        target.StripMonitor = monitor;
        target.StripX = x;
        target.StripY = y;
        if (draft == null && Save(target)) saved = target;
        if (editor != null) editor.SyncPosition();
        ApplySettings();
    }

    internal void OpenStripMenu(Point point)
    {
        details.Close();
        resources.Close();
        media.Close();
        stripMenu.Show(point, ToolStripDropDownDirection.AboveLeft);
    }

    internal void OpenSettings()
    {
        resources.Close();
        media.Close();
        details.Close();
        if (editor == null)
        {
            draft = saved.Copy();
            editor = new SettingsWindow(this);
            editor.FormClosed += (sender, args) =>
            {
                editor = null;
                draft = null;
                ApplySettings();
            };
            editor.Show();
        }
        if (editor.WindowState == FormWindowState.Minimized) editor.WindowState = FormWindowState.Normal;
        editor.BringToFront();
        editor.Activate();
    }

    internal void ResetSettings()
    {
        draft = new StripSettings { Language = draft.Language };
        ApplySettings();
    }

    internal bool Commit(bool startWithWindows)
    {
        var candidate = draft.Copy();

        bool previousStartup;
        try
        {
            previousStartup = StartupRegistration.Enabled;
            if (previousStartup != startWithWindows) StartupRegistration.SetEnabled(startWithWindows);
        }
        catch (Exception error)
        {
            if (!StartupRegistration.IsAccessError(error)) throw;
            MessageBox.Show(editor, Translate("Не вдалося змінити автозапуск Windows."), "Charge Strip", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        if (!Save(candidate))
        {
            try
            {
                if (StartupRegistration.Enabled != previousStartup)
                    StartupRegistration.SetEnabled(previousStartup);
            }
            catch (Exception error)
            {
                if (!StartupRegistration.IsAccessError(error)) throw;
                MessageBox.Show(editor, Translate("Не вдалося відновити попередній стан автозапуску."), "Charge Strip", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }

        saved = candidate;
        return true;
    }

    private bool Save(StripSettings settings)
    {
        try { settings.Save(); return true; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException) { }
        MessageBox.Show(editor, Translate("Не вдалося зберегти налаштування."), "Charge Strip", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return false;
    }

    private async Task RefreshReadings()
    {
        if (disposed || polling) return;
        polling = true;
        try
        {
            rows = await Task.Run(() => Batteries.Read());
            if (disposed) return;
            refreshed = DateTime.Now;
            ApplySettings();
        }
        finally { polling = false; }
    }

    internal void OpenDetails(Point point)
    {
        resources.Close();
        media.Close();
        if (details.Visible) { details.Close(); return; }
        details.Show(point, ToolStripDropDownDirection.AboveLeft);
    }

    private void FillDetails()
    {
        details.SuspendLayout();
        while (details.Items.Count > 0)
        {
            var item = details.Items[0];
            details.Items.RemoveAt(0);
            item.Dispose();
        }
        details.BackColor = StripSettings.PopupColor(Current.Background, Current.Alpha);
        details.ForeColor = StripSettings.ColorOf(Current.Text);
        var header = new ToolStripMenuItem("Charge Strip  ⚙", null, (sender, args) => OpenSettings());
        header.Font = popupHeaderFont;
        details.Items.Add(header);
        details.Items.Add(new ToolStripSeparator());
        var readings = Readings;
        if (readings.Count == 0)
            details.Items.Add(new ToolStripLabel(Translate(refreshed.HasValue ? "Немає доступних показників заряду" : "Зчитування показників…")));
        foreach (BatteryReading row in readings)
        {
            bool icons = Current.DisplayMode != 2;
            bool numbers = Current.DisplayMode != 1;
            details.Items.Add(new TrayRow(Translate(row.Name), Translate(row.Status), row,
                Current, icons, numbers));
        }
        if (refreshed.HasValue)
            details.Items.Add(new ToolStripLabel(Translate("Оновлено о ") + refreshed.Value.ToString("HH:mm:ss")) { ForeColor = Muted });
        details.Items.Add(new ToolStripSeparator());
        details.Items.Add(Translate("Оновити"), null, async (sender, args) => await RefreshReadings());
        details.ResumeLayout();
    }

    private sealed class TrayRow : ToolStripLabel
    {
        private readonly string title, status, glyph, level;
        private readonly Color text, accent;
        private readonly float factor;
        private readonly bool hasIcon;

        internal TrayRow(string title, string status, BatteryReading row, StripSettings settings, bool icon, bool number)
        {
            this.title = title;
            this.status = status;
            hasIcon = icon;
            glyph = row.Icon;
            level = number ? row.Level : "";
            Text = title + " · " + row.Level + " · " + status;
            AccessibleName = Text;
            text = StripSettings.ColorOf(settings.Text);
            accent = StripSettings.ColorOf(row.Percent <= 25 ? settings.Low : settings.Icons);
            factor = settings.Zoom / 100f;
            AutoSize = false;
            Size = new Size((int)(338 * factor), (int)((42 + settings.Gap) * factor));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var name = new Font("Segoe UI", 9 * factor, FontStyle.Regular))
            using (var value = new Font("Segoe UI", 10 * factor, FontStyle.Bold))
            using (var small = new Font("Segoe UI", 8 * factor, FontStyle.Regular))
            using (var icon = new Font("Segoe MDL2 Assets", 11 * factor, FontStyle.Regular))
            {
                int left = hasIcon ? (int)(33 * factor) : 8;
                int width = Width - left - (level.Length == 0 ? 10 : (int)(56 * factor));
                const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                if (hasIcon) TextRenderer.DrawText(e.Graphics, glyph, icon, new Rectangle(4, 0, left - 5, Height), accent, line | TextFormatFlags.HorizontalCenter);
                TextRenderer.DrawText(e.Graphics, title, name, new Rectangle(left, 2, width, Height / 2), text, line);
                TextRenderer.DrawText(e.Graphics, status, small, new Rectangle(left, Height / 2, width, Height / 2), text, line);
                if (level.Length != 0)
                    TextRenderer.DrawText(e.Graphics, level, value, new Rectangle(Width - (int)(55 * factor), 0, (int)(51 * factor), Height), accent, line | TextFormatFlags.Right);
            }
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private static readonly Color Highlight = Color.FromArgb(56, 67, 83);
        private static readonly Color Border = Color.FromArgb(65, 74, 88);
        public override Color MenuItemSelected { get { return Highlight; } }
        public override Color MenuItemBorder { get { return Highlight; } }
        public override Color MenuBorder { get { return Border; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return Border; } }
        public override Color ToolStripDropDownBackground { get { return Background; } }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            if (editor != null) editor.Dispose();
            timer.Dispose();
            inputTimer.Dispose();
            surface.Dispose();
            resources.Dispose();
            media.Dispose();
            details.Dispose();
            popupHeaderFont.Dispose();
            stripMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class Native
{
    internal static void RoundCorners(IntPtr window)
    {
        int preference = 2;
        DwmSetWindowAttribute(window, 33, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, uint attribute, ref int value, uint size);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
