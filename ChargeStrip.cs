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
    private readonly ContextMenuStrip stripMenu = new ContextMenuStrip();
    private readonly ResourcesModule resources;
    private readonly MediaModule media;
    private readonly ModuleStrip surface;
    private ReadingsFlyout readingsFlyout;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 12000 };
    private readonly System.Windows.Forms.Timer inputTimer = new System.Windows.Forms.Timer { Interval = 150 };
    private string menuLanguage;
    private List<BatteryReading> rows = new List<BatteryReading>();
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
        resources = new ResourcesModule(() => Current);
        media = new MediaModule(() => Current);
        surface = new ModuleStrip(this);
        media.Changed += () => { surface.UpdateDrawing(); if (editor != null) editor.SyncReadings(); };
        resources.Changed += () => { UpdateExistingIsland(); surface.UpdateDrawing(); if (readingsFlyout != null) readingsFlyout.Invalidate(); if (editor != null) editor.SyncReadings(); };
        stripMenu.Items.Add("Налаштування", null, (sender, args) => OpenSettings());
        stripMenu.Items.Add("Оновити", null, async (sender, args) => await RefreshReadings());
        stripMenu.Items.Add("Вийти", null, (sender, args) => ExitThread());

        ApplySettings();
        bool initialRead = true;
        inputTimer.Tick += async (sender, args) =>
        {
            if (Current.UseExistingIsland) ReadIslandClick();
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
        if (surface.Expanded != null && !Current.ModuleVisible(surface.Expanded)) CollapseIsland();
        resources.Apply();
        media.Apply(!Current.UseExistingIsland && Current.ModuleVisible("media"));
        UpdateExistingIsland();
        SyncModuleSurface();
        if (readingsFlyout != null) readingsFlyout.Invalidate();
        if (editor != null) editor.SyncReadings();
    }

    internal MediaSnapshot MediaSnapshot { get { return media.Snapshot; } }
    internal Bitmap MediaArtwork { get { return media.Artwork; } }

    private void UpdateExistingIsland()
    {
        string path = Path.Combine(StripSettings.Folder, "island-readings.txt");
        try
        {
            if (!Current.UseExistingIsland) { File.Delete(path); return; }
            var battery = Current.ModuleVisible("batteries")
                ? Readings.Take(2).Select(row => row.Percent.GetValueOrDefault(-1).ToString()) : Enumerable.Empty<string>();
            var metrics = Current.ModuleVisible("resources") ? Current.VisibleMetrics(true).ToArray() : new ResourceMetric[0];
            if (Current.ModuleVisible("resources") && metrics.Length == 0) metrics = Current.VisibleMetrics(false).Take(2).ToArray();
            var lines = new List<string> { string.Join(",", battery), Current.Icons, Current.Text };
            foreach (var metric in metrics)
                lines.Add(ResourceMetrics.Label(metric) + "\t" + ResourceReading.Value(metric, true));
            if (Current.ModuleVisible("resources")) lines.Add("R");
            Directory.CreateDirectory(StripSettings.Folder);
            string pending = path + ".tmp";
            File.WriteAllLines(pending, lines);
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ReadIslandClick()
    {
        string path = Path.Combine(StripSettings.Folder, "island-click.txt");
        string request;
        try
        {
            if (!File.Exists(path)) return;
            request = File.ReadAllText(path);
            bool fresh = DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(3);
            File.Delete(path);
            if (!fresh) return;
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        string[] fields = request.Split(',');
        int x, y;
        if (fields.Length != 3 || (fields[0] != "1" && fields[0] != "2") ||
            !int.TryParse(fields[1], out x) || !int.TryParse(fields[2], out y)) return;
        bool battery = fields[0] == "1";
        bool alreadyOpen = readingsFlyout != null && readingsFlyout.Battery == battery;
        if (readingsFlyout != null) readingsFlyout.Close();
        if (alreadyOpen || !Current.ModuleVisible(battery ? "batteries" : "resources")) return;
        readingsFlyout = new ReadingsFlyout(this, battery, new Point(x, y));
        readingsFlyout.FormClosed += (sender, args) => { if (ReferenceEquals(readingsFlyout, sender)) readingsFlyout = null; };
        readingsFlyout.Show();
        readingsFlyout.Activate();
    }

    internal void SyncModuleSurface()
    {
        if (disposed) return;
        if (Current.UseExistingIsland)
        {
            CollapseIsland();
            surface.Hide();
            return;
        }
        Rectangle old = surface.VisibleBounds;
        surface.Arrange();
        if (old != surface.VisibleBounds)
        {
            stripMenu.Close();
            CollapseIsland();
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
        if (id == "batteries") return;
        if (surface.Expanded == id) { CollapseIsland(); return; }
        media.Close();
        if (id == "media") media.Open(surface);
        surface.SetExpanded(id);
    }

    internal void CollapseIsland()
    {
        media.Close();
        if (surface.Expanded != null) surface.SetExpanded(null);
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
        CollapseIsland();
        stripMenu.Show(point, ToolStripDropDownDirection.AboveLeft);
    }

    internal void OpenSettings()
    {
        CollapseIsland();
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
            ApplySettings();
        }
        finally { polling = false; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            if (editor != null) editor.Dispose();
            if (readingsFlyout != null) readingsFlyout.Dispose();
            timer.Dispose();
            inputTimer.Dispose();
            surface.Dispose();
            resources.Dispose();
            media.Dispose();
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
