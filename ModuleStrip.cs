using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class ModuleStrip : Form
{
    private const int OuterPadding = 5, SectionPadding = 8, HeightUnits = 34;
    private readonly Strip owner;
    private readonly ToolTip toolTip = new ToolTip();
    private readonly Timer timer = new Timer { Interval = 180 };
    private readonly List<KeyValuePair<string, Rectangle>> regions = new List<KeyValuePair<string, Rectangle>>();
    private Rectangle bar, rowBounds;
    private string expanded;
    private int panelHeight;
    private readonly Timer animation = new Timer { Interval = 15 };
    internal string Expanded { get { return expanded; } }

    internal void SetExpanded(string module)
    {
        toolTip.Hide(this);
        expanded = module;
        animation.Start();
        Arrange();
    }
    private int appRight, dockRight;
    private bool scanning, measured, dragging;
    private Point pointer, origin;
    internal Rectangle VisibleBounds { get { return Visible ? rowBounds : Rectangle.Empty; } }

    internal Screen PositionScreen(string monitor)
    {
        return Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName == monitor)
            ?? (bar.IsEmpty ? Screen.PrimaryScreen : Screen.FromRectangle(bar));
    }

    internal ModuleStrip(Strip owner)
    {
        this.owner = owner;
        Text = "Charge Strip · modules";
        AccessibleName = "Charge Strip";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        animation.Tick += (sender, args) => Arrange();
        Cursor = Cursors.Hand;
        timer.Tick += async (sender, args) => await MeasureTaskbar();
        timer.Start();
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= 0x80; // Tool window.
            return p;
        }
    }

    private async Task MeasureTaskbar()
    {
        if (scanning || IsDisposed) return;
        scanning = true;
        timer.Interval = 1250;
        try
        {
            var layout = await Task.Run(() => StripDesktop.Measure());
            if (IsDisposed) return;
            bar = layout.Bar;
            appRight = layout.AppRight;
            dockRight = layout.DockRight;
            measured = true;
            owner.SyncModuleSurface();
        }
        finally { scanning = false; }
    }

    internal bool Arrange()
    {
        var settings = owner.Current;
        var modules = settings.ModuleOrder.Where(settings.ModuleVisible).ToArray();
        bool docked = settings.IsDocked;
        var screen = PositionScreen(docked ? null : settings.StripMonitor);
        if (!measured || modules.Length == 0 || StripDesktop.FullScreen(screen.Bounds) ||
            (docked && (bar.Height < 24 || dockRight <= appRight)))
        {
            if (Visible) Hide();
            return false;
        }
        int available = docked ? dockRight - appRight : screen.Bounds.Width;
        float requested = settings.Zoom / 100f * StripDesktop.Dpi(screen.Bounds);
        int maxHeight = docked ? bar.Height - 4 : screen.Bounds.Height;
        float effective = Math.Min(requested, maxHeight / (float)HeightUnits);
        int resourceCount = settings.VisibleMetrics(true).Count();
        effective = Math.Min(effective, available / StripWidth(modules, resourceCount, true));
        if (effective < .55f)
        {
            if (Visible) Hide();
            return false;
        }

        if (!Capture)
        {
            Rectangle row;
            int width = Math.Min(available, (int)Math.Ceiling(StripWidth(modules, resourceCount, false) * effective));
            int height = (int)Math.Round(HeightUnits * effective);
            if (docked)
                row = new Rectangle(dockRight - width, bar.Top + (bar.Height - height) / 2, width, height);
            else
            {
                Rectangle area = screen.Bounds;
                int x = settings.StripX ?? area.Width - width;
                int y = settings.StripY ?? (area.Contains(bar.Location) && !bar.IsEmpty
                    ? bar.Top - area.Top + (bar.Height - height) / 2 : screen.WorkingArea.Bottom - area.Top - height);
                row = new Rectangle(Limit(area.Left + StripSettings.Clamp(x, 0, area.Width - 1),
                    area.Top + StripSettings.Clamp(y, 0, area.Height - 1), area, width, height), new Size(width, height));
            }
            rowBounds = row;
            int target = expanded == null ? 0 : (int)((expanded == "media" ? 410 : ResourcePanel.ViewHeight(settings) + 8) * requested);
            target = Math.Min(target, Math.Max(0, row.Top - screen.WorkingArea.Top));
            int delta = target - panelHeight;
            panelHeight += Math.Sign(delta) * (int)Math.Ceiling(Math.Abs(delta) * .36);
            if (panelHeight == target) animation.Stop();
            int wide = Math.Max(row.Width, Math.Min(row.Right - screen.Bounds.Left, (int)(350 * requested)));
            int panelWidth = row.Width + (wide - row.Width) * Math.Min(panelHeight, 100) / 100;
            Bounds = new Rectangle(row.Right - panelWidth, row.Top - panelHeight, panelWidth, row.Height + panelHeight);
            if (Controls.Count > 0)
            {
                Controls[0].Bounds = new Rectangle(2, 2, Width - 4, Math.Max(0, panelHeight - 3));
                Controls[0].Visible = panelHeight >= target && target > 0;
            }
        }
        BackColor = StripAppearance.Surface(settings);
        Invalidate();
        if (!Visible) Show();
        StripDesktop.KeepAboveTaskbar(Handle, rowBounds);
        return true;
    }

    private static Point Limit(int x, int y, Rectangle area, int width, int height)
    {
        return new Point(StripSettings.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)),
            StripSettings.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)));
    }

    internal void UpdateDrawing() { if (Visible && !IsDisposed) Invalidate(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 2 || Height < 2) return;
        using (var shape = StripAppearance.Rounded(ClientRectangle, StripAppearance.CornerRadius))
        {
            Region previous = Region;
            Region = new Region(shape);
            if (previous != null) previous.Dispose();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Rectangle row = new Rectangle(rowBounds.Left - Left, rowBounds.Top - Top, rowBounds.Width, rowBounds.Height);
        regions.Clear();
        Draw(e.Graphics, row, owner, regions, false);
        if (expanded == "resources" && panelHeight > 5)
            ResourcePanel.Draw(e.Graphics, new Rectangle(12, 5, Width - 24, panelHeight - 5), owner.ResourceReading, owner.Current, true);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var contour = StripAppearance.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), StripAppearance.CornerRadius))
        using (var ink = new Pen(Color.FromArgb(105, StripSettings.ColorOf(owner.Current.Icons))))
            e.Graphics.DrawPath(ink, contour);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape && expanded != null) { owner.CollapseIsland(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private static float SectionWidth(string id, int resourceCount, bool compact)
    {
        float content = id == "media" ? (compact ? 54 : 121) :
            id == "resources" ? 20 : 36;
        return content + 2 * SectionPadding;
    }

    private static float StripWidth(string[] modules, int resourceCount, bool compact)
    {
        return 2 * OuterPadding + modules.Sum(id => SectionWidth(id, resourceCount, compact));
    }

    internal static int PreviewWidth(StripSettings settings, int available, int height)
    {
        var modules = settings.ModuleOrder.Where(settings.ModuleVisible).ToArray();
        if (modules.Length == 0) return 0;
        return Math.Min(available, (int)Math.Ceiling(StripWidth(modules, settings.VisibleMetrics(true).Count(), false)
            * height / HeightUnits));
    }

    internal static void Draw(Graphics graphics, Rectangle area, Strip owner, List<KeyValuePair<string, Rectangle>> targets = null, bool drawBackground = true)
    {
        var settings = owner.Current;
        var modules = settings.ModuleOrder.Where(settings.ModuleVisible).ToArray();
        if (modules.Length == 0 || area.Width < 2 || area.Height < 2) return;
        int resourceCount = settings.VisibleMetrics(true).Count();
        float minimum = StripWidth(modules, resourceCount, true);
        float factor = Math.Min(area.Height / (float)HeightUnits, area.Width / minimum);
        float units = area.Width / factor;
        float top = area.Y + (area.Height - HeightUnits * factor) / 2;
        float flexibility = StripWidth(modules, resourceCount, false) - minimum;
        float surplus = Math.Max(0, units - minimum);
        float balance = flexibility == 0 ? 0 : Math.Min(1, surplus / flexibility);
        float extra = Math.Max(0, surplus - flexibility) / modules.Length;
        float left = OuterPadding;
        var state = graphics.Save();
        graphics.TranslateTransform(area.X, top);
        graphics.ScaleTransform(factor, factor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Color text = StripSettings.ColorOf(settings.Text);
        Color accent = StripSettings.ColorOf(settings.Icons);
        using (var path = StripAppearance.Rounded(new RectangleF(.65f, .65f, units - 1.3f, HeightUnits - 1.3f),
            StripAppearance.CornerRadius))
        using (var paint = new SolidBrush(StripAppearance.Surface(settings)))
        using (var outline = new Pen(Color.FromArgb(105, accent), .9f))
        using (var divider = new Pen(Color.FromArgb(63, accent), .8f))
        {
            if (drawBackground) { graphics.FillPath(paint, path); graphics.DrawPath(outline, path); }
            foreach (string id in modules)
            {
                float compact = SectionWidth(id, resourceCount, true);
                float width = compact + (SectionWidth(id, resourceCount, false) - compact) * balance + extra;
                var content = new RectangleF(left + SectionPadding, 4, width - 2 * SectionPadding, 26);
                if (targets != null)
                    targets.Add(new KeyValuePair<string, Rectangle>(id, Rectangle.Round(new RectangleF(area.X + left * factor,
                        top + 2 * factor, width * factor, (HeightUnits - 4) * factor))));
                if (id == "media") DrawMedia(graphics, content, owner, text, accent);
                else if (id == "resources") ResourcesModule.DrawChip(graphics,
                    new RectangleF(content.X + (content.Width - 20) / 2, content.Y + 3, 20, 20), accent);
                else DrawBatteries(graphics, content, owner.Readings, text, accent, StripSettings.ColorOf(settings.Low));
                left += width;
                if (id != modules[modules.Length - 1]) graphics.DrawLine(divider, left, 8, left, HeightUnits - 8);
            }
        }
        graphics.Restore(state);
    }

    private static void DrawBatteries(Graphics graphics, RectangleF bounds, IList<BatteryReading> readings,
        Color text, Color accent, Color low)
    {
        const int rowHeight = 13, labelWidth = 36;
        int count = Math.Max(1, Math.Min(2, readings.Count));
        float x = bounds.X + (bounds.Width - labelWidth) / 2;
        float y = bounds.Y + (bounds.Height - rowHeight) / 2;
        {
            for (int i = 0; i < count; i++, x += 20)
            {
                int? percent = i < readings.Count ? readings[i].Percent : null;
                Color batteryColor = percent <= 25 ? low : accent;
                using (var line = new Pen(batteryColor, 1))
                using (var fill = new SolidBrush(batteryColor))
                using (var battery = StripAppearance.Rounded(new RectangleF(x + .5f, y + 3.5f, 12, 6), 1.5f))
                {
                    graphics.DrawPath(line, battery);
                    graphics.DrawLine(line, x + 14, y + 5, x + 14, y + 8);
                    if (percent > 0)
                        graphics.FillRectangle(fill, x + 2, y + 5, 9 * Math.Min(100, percent.Value) / 100f, 3);
                }
            }
        }
    }

    private static void DrawMedia(Graphics graphics, RectangleF bounds, Strip owner, Color text, Color accent)
    {
        const int coverSize = 22, textOffset = 27;
        var snapshot = owner.MediaSnapshot;
        var cover = new RectangleF(bounds.X, bounds.Y + (bounds.Height - coverSize) / 2, coverSize, coverSize);
        using (var path = StripAppearance.Rounded(cover, 4))
        using (var titleFont = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(text))
        using (var glyph = new SolidBrush(accent))
        using (var placeholder = new SolidBrush(Color.FromArgb(22, accent)))
        using (var ellipsis = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            var image = owner.MediaArtwork;
            if (image == null)
            {
                graphics.FillPath(placeholder, path);
                graphics.DrawString("♪", titleFont, glyph, cover, center);
            }
            else
            {
                var state = graphics.Save();
                graphics.SetClip(path, CombineMode.Intersect);
                float side = Math.Min(image.Width, image.Height);
                graphics.DrawImage(image, cover, new RectangleF((image.Width - side) / 2, (image.Height - side) / 2, side, side), GraphicsUnit.Pixel);
                graphics.Restore(state);
            }
            string title = string.IsNullOrWhiteSpace(snapshot.Title) ? owner.Translate("Медіа") : snapshot.Title;
            float x = bounds.X + textOffset, width = bounds.Width - textOffset;
            graphics.DrawString(title, titleFont, ink, new RectangleF(x, bounds.Y + 6, width, 15), ellipsis);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        toolTip.Hide(this);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        pointer = StripDesktop.MessagePoint();
        origin = Location;
        Capture = true;
        dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var tile = regions.FirstOrDefault(region => region.Value.Contains(e.Location));
        if (!Capture || e.Button != MouseButtons.Left)
        {
            string hint = !string.IsNullOrEmpty(tile.Key) ? owner.ModuleHint(tile.Key) :
                owner.Translate(owner.Current.IsDocked ? "Ліворуч від системного трея" : "Перетягніть смужку");
            if (toolTip.GetToolTip(this) != hint) toolTip.SetToolTip(this, hint);
            return;
        }
        Point cursor = StripDesktop.MessagePoint();
        int dx = cursor.X - pointer.X, dy = cursor.Y - pointer.Y;
        if (!dragging && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        dragging = true;
        if (owner.Current.IsDocked || panelHeight != 0) return;
        Location = Limit(origin.X + dx, origin.Y + dy, Screen.FromPoint(cursor).Bounds, Width, Height);
        rowBounds.Location = Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) { owner.OpenStripMenu(new Point(PointToScreen(e.Location).X, Top - 4)); return; }
        if (e.Button != MouseButtons.Left || !Capture) return;
        Capture = false;
        if (dragging)
        {
            if (owner.Current.IsDocked || panelHeight != 0) return;
            var screen = Screen.FromRectangle(Bounds);
            owner.SaveModulePosition(screen.DeviceName, Left - screen.Bounds.Left, Top - screen.Bounds.Top);
            return;
        }
        var tile = regions.FirstOrDefault(region => region.Value.Contains(e.Location));
        if (!string.IsNullOrEmpty(tile.Key)) owner.OpenModule(tile.Key, RectangleToScreen(tile.Value));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); animation.Dispose(); toolTip.Dispose(); }
        base.Dispose(disposing);
    }
}

internal static class StripAppearance
{
    internal const int CornerRadius = 11;

    internal static Color Surface(StripSettings settings)
    {
        return StripSettings.PopupColor(settings.Background, settings.Alpha);
    }

    internal static Color Mix(Color background, Color tone, double amount)
    {
        return Color.FromArgb((int)(background.R + (tone.R - background.R) * amount),
            (int)(background.G + (tone.G - background.G) * amount), (int)(background.B + (tone.B - background.B) * amount));
    }

    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal static class StripDesktop
{
    internal struct Layout { public Rectangle Bar; public int AppRight, DockRight; }
    private const int TrayGap = 6;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    internal static Layout Measure()
    {
        IntPtr shell = FindWindow("Shell_TrayWnd", null);
        Rect outer, notify;
        if (shell == IntPtr.Zero || !GetWindowRect(shell, out outer) || outer.Bottom - outer.Top < 24 ||
            outer.Bottom - outer.Top > outer.Right - outer.Left) return new Layout();
        var layout = new Layout { Bar = Rectangle.FromLTRB(outer.Left, outer.Top, outer.Right, outer.Bottom) };
        IntPtr tray = FindWindowEx(shell, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out notify) || notify.Left <= outer.Left ||
            notify.Left >= outer.Right || notify.Right <= notify.Left) return layout;
        int last = 0, mirroredRight = 0, firstTrayIcon = int.MaxValue;
        var primary = Screen.FromRectangle(layout.Bar);
        try
        {
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"));
            var buttons = AutomationElement.RootElement.FindAll(TreeScope.Descendants, condition);
            foreach (AutomationElement button in buttons)
            {
                try
                {
                    var current = button.Current;
                    var bounds = current.BoundingRectangle;
                    if (!bounds.IsEmpty && !current.IsOffscreen && current.ClassName == "Taskbar.TaskListButtonAutomationPeer"
                        && bounds.Left >= outer.Right)
                    {
                        var display = Screen.FromPoint(new Point((int)bounds.Left, (int)bounds.Top)).Bounds;
                        if (display.Width == primary.Bounds.Width && bounds.Bottom >= display.Bottom - layout.Bar.Height)
                            mirroredRight = Math.Max(mirroredRight, outer.Left + (int)Math.Ceiling(bounds.Right) - display.Left);
                    }
                    if (bounds.IsEmpty || current.IsOffscreen || bounds.Top < outer.Top - 8 ||
                        bounds.Bottom > outer.Bottom + 8 || bounds.Right <= outer.Left || bounds.Right > outer.Right)
                        continue;
                    if (current.AutomationId == "SystemTrayIcon")
                    {
                        if (bounds.Left >= notify.Left)
                            firstTrayIcon = Math.Min(firstTrayIcon, (int)Math.Floor(bounds.Left));
                    }
                    else last = Math.Max(last, (int)Math.Ceiling(bounds.Right));
                }
                catch (ElementNotAvailableException) { }
            }
        }
        catch (ElementNotAvailableException) { }
        catch (COMException) { }
        catch (InvalidOperationException) { }
        if (last == 0) last = mirroredRight;
        if (last != 0)
        {
            layout.AppRight = last + 6;
            // TrayNotifyWnd can start before the visible tray on a customized taskbar.
            layout.DockRight = (firstTrayIcon <= notify.Left + 24 ? firstTrayIcon : notify.Left) - TrayGap;
        }
        return layout;
    }

    internal static bool FullScreen(Rectangle screen)
    {
        IntPtr active = GetForegroundWindow();
        Rect rect;
        if (active == IntPtr.Zero || active == GetDesktopWindow() || active == GetShellWindow() ||
            !GetWindowRect(active, out rect)) return false;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    internal static float Dpi(Rectangle screen)
    {
        uint x, y;
        IntPtr monitor = MonitorFromPoint(screen.Location, 2);
        return GetDpiForMonitor(monitor, 0, out x, out y) == 0 && x > 0 ? x / 96f : 1;
    }

    internal static Point MessagePoint()
    {
        uint p = GetMessagePos();
        return new Point(unchecked((short)p), unchecked((short)(p >> 16)));
    }

    internal static void KeepAboveTaskbar(IntPtr window, Rectangle bounds)
    {
        IntPtr hit = WindowFromPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
        if (hit == window || hit == IntPtr.Zero) return;
        if (GetAncestor(hit, 2) == FindWindow("Shell_TrayWnd", null))
            SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x213);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetMessagePos();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

}
