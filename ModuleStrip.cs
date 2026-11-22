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
    private Rectangle bar;
    private int appRight, dockRight;
    private bool scanning, measured, dragging;
    private Point pointer, origin;
    internal Rectangle VisibleBounds { get { return Visible ? Bounds : Rectangle.Empty; } }

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
            p.ExStyle |= 0x80000 | 0x80; // Layered tool window.
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
            int width = Math.Min(available, (int)Math.Ceiling(StripWidth(modules, resourceCount, false) * effective));
            int height = (int)Math.Round(HeightUnits * effective);
            if (docked)
                Bounds = new Rectangle(dockRight - width, bar.Top + (bar.Height - height) / 2, width, height);
            else
            {
                Rectangle area = screen.Bounds;
                int x = settings.StripX ?? area.Width - width;
                int y = settings.StripY ?? (area.Contains(bar.Location) && !bar.IsEmpty
                    ? bar.Top - area.Top + (bar.Height - height) / 2 : screen.WorkingArea.Bottom - area.Top - height);
                Bounds = new Rectangle(Limit(area.Left + StripSettings.Clamp(x, 0, area.Width - 1),
                    area.Top + StripSettings.Clamp(y, 0, area.Height - 1), area, width, height), new Size(width, height));
            }
        }
        PaintLayer();
        if (!Visible) Show();
        StripDesktop.KeepAboveTaskbar(Handle, Bounds);
        return true;
    }

    private static Point Limit(int x, int y, Rectangle area, int width, int height)
    {
        return new Point(StripSettings.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)),
            StripSettings.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)));
    }

    internal void UpdateDrawing()
    {
        if (Visible && !IsDisposed) PaintLayer();
    }

    private void PaintLayer()
    {
        using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                regions.Clear();
                Draw(g, ClientRectangle, owner, regions);
            }
            StripDesktop.Present(Handle, bmp, Bounds);
        }
    }

    private static float SectionWidth(string id, int resourceCount, bool compact)
    {
        float content = id == "media" ? (compact ? 54 : 121) :
            id == "resources" ? ResourcesModule.TileWidth(resourceCount, compact) : (compact ? 42 : 45);
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

    internal static void Draw(Graphics graphics, Rectangle area, Strip owner, List<KeyValuePair<string, Rectangle>> targets = null)
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
            graphics.FillPath(paint, path);
            graphics.DrawPath(outline, path);
            foreach (string id in modules)
            {
                float compact = SectionWidth(id, resourceCount, true);
                float width = compact + (SectionWidth(id, resourceCount, false) - compact) * balance + extra;
                var content = new RectangleF(left + SectionPadding, 4, width - 2 * SectionPadding, 26);
                if (targets != null)
                    targets.Add(new KeyValuePair<string, Rectangle>(id, Rectangle.Round(new RectangleF(area.X + left * factor,
                        top + 2 * factor, width * factor, (HeightUnits - 4) * factor))));
                if (id == "media") DrawMedia(graphics, content, owner, text, accent);
                else if (id == "resources") ResourcesModule.DrawTile(graphics, content, owner.ResourceReading, settings);
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
        const int rowHeight = 13, labelWidth = 42;
        int count = Math.Max(1, Math.Min(2, readings.Count));
        float x = bounds.X + (bounds.Width - labelWidth) / 2;
        float y = bounds.Y + (bounds.Height - count * rowHeight) / 2;
        using (var number = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(text))
        using (var row = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, LineAlignment = StringAlignment.Center })
        {
            for (int i = 0; i < count; i++, y += rowHeight)
            {
                int? percent = i < readings.Count ? readings[i].Percent : null;
                string value = i < readings.Count ? readings[i].Level : "—";
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
                graphics.DrawString(value, number, ink, new RectangleF(x + 17, y, 30, rowHeight), row);
            }
        }
    }

    private static void DrawMedia(Graphics graphics, RectangleF bounds, Strip owner, Color text, Color accent)
    {
        const int coverSize = 22, textOffset = 29;
        var snapshot = owner.MediaSnapshot;
        var cover = new RectangleF(bounds.X, bounds.Y + (bounds.Height - coverSize) / 2, coverSize, coverSize);
        using (var path = StripAppearance.Rounded(cover, 4))
        using (var titleFont = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var subtitleFont = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(text))
        using (var dim = new SolidBrush(Color.FromArgb(172, text)))
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
            string subtitle = string.IsNullOrWhiteSpace(snapshot.Artist) ? MediaSnapshot.SourceLabel(snapshot.Source) : snapshot.Artist;
            float x = bounds.X + textOffset, width = bounds.Width - textOffset;
            float titleY = string.IsNullOrWhiteSpace(subtitle) ? bounds.Y + 6 : bounds.Y;
            graphics.DrawString(title, titleFont, ink, new RectangleF(x, titleY, width, 15), ellipsis);
            if (!string.IsNullOrWhiteSpace(subtitle))
                graphics.DrawString(subtitle, subtitleFont, dim, new RectangleF(x, bounds.Y + 14, width, 12), ellipsis);
        }
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
        if (owner.Current.IsDocked) return;
        Location = Limit(origin.X + dx, origin.Y + dy, Screen.FromPoint(cursor).Bounds, Width, Height);
        PaintLayer();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) { owner.OpenStripMenu(new Point(PointToScreen(e.Location).X, Top - 4)); return; }
        if (e.Button != MouseButtons.Left || !Capture) return;
        Capture = false;
        if (dragging)
        {
            if (owner.Current.IsDocked) return;
            var screen = Screen.FromRectangle(Bounds);
            owner.SaveModulePosition(screen.DeviceName, Left - screen.Bounds.Left, Top - screen.Bounds.Top);
            return;
        }
        var tile = regions.FirstOrDefault(region => region.Value.Contains(e.Location));
        if (!string.IsNullOrEmpty(tile.Key)) owner.OpenModule(tile.Key, RectangleToScreen(tile.Value));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); toolTip.Dispose(); }
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
    [StructLayout(LayoutKind.Sequential)] private struct Blend { public byte Op, Flags, Alpha, Format; }

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
        int last = 0, firstTrayIcon = int.MaxValue;
        try
        {
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"));
            var buttons = AutomationElement.FromHandle(shell).FindAll(TreeScope.Descendants, condition);
            foreach (AutomationElement button in buttons)
            {
                try
                {
                    var current = button.Current;
                    var bounds = current.BoundingRectangle;
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
        if (last != 0)
        {
            layout.AppRight = last + 6;
            // TrayNotifyWnd can start before the visible tray on a customized taskbar.
            layout.DockRight = (firstTrayIcon == int.MaxValue ? notify.Left : firstTrayIcon) - TrayGap;
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

    internal static void Present(IntPtr window, Bitmap bitmap, Rectangle bounds)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr context = CreateCompatibleDC(screen);
        IntPtr pixels = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr previous = SelectObject(context, pixels);
        try
        {
            Point position = bounds.Location, origin = Point.Empty;
            Size size = bounds.Size;
            var blend = new Blend { Alpha = 255, Format = 1 };
            UpdateLayeredWindow(window, screen, ref position, ref size, context, ref origin, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(context, previous);
            DeleteObject(pixels);
            DeleteDC(context);
            ReleaseDC(IntPtr.Zero, screen);
        }
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
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr dst, ref Point pos, ref Size size, IntPtr src, ref Point origin, uint key, ref Blend blend, uint flags);
}
