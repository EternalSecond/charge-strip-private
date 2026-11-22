using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

internal sealed class ResourceReading
{
    internal double? Cpu, Gpu, Disk, Download, Upload;
    internal ulong? MemoryUsed, MemoryTotal;
}

internal sealed class ResourceSampler : IDisposable
{
    private ulong previousIdle, previousKernel, previousUser;
    private bool hasCpu;
    private long previousNetworkTime;
    private Dictionary<string, long[]> previousNetwork = new Dictionary<string, long[]>();
    private readonly GpuSampler gpu = new GpuSampler();

    internal ResourceReading Read()
    {
        var result = new ResourceReading();
        ulong idle, kernel, user;
        if (GetSystemTimes(out idle, out kernel, out user))
        {
            if (hasCpu && idle >= previousIdle && kernel >= previousKernel && user >= previousUser)
            {
                ulong total = (kernel - previousKernel) + (user - previousUser);
                if (total > 0) result.Cpu = Math.Max(0, Math.Min(100, 100 - 100.0 * (idle - previousIdle) / total));
            }
            previousIdle = idle;
            previousKernel = kernel;
            previousUser = user;
            hasCpu = true;
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical >= memory.AvailablePhysical)
        {
            result.MemoryTotal = memory.TotalPhysical;
            result.MemoryUsed = memory.TotalPhysical - memory.AvailablePhysical;
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
            long total = drive.TotalSize;
            if (total > 0) result.Disk = 100 - 100.0 * drive.TotalFreeSpace / total;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        ReadNetwork(result);
        result.Gpu = gpu.Read();
        return result;
    }

    private void ReadNetwork(ResourceReading result)
    {
        var current = new Dictionary<string, long[]>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var stats = adapter.GetIPStatistics();
                    current[adapter.Id] = new[] { stats.BytesReceived, stats.BytesSent };
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { return; }

        long now = Stopwatch.GetTimestamp();
        if (previousNetworkTime != 0 && now > previousNetworkTime)
        {
            double down = 0, up = 0;
            foreach (var adapter in current)
            {
                long[] before;
                if (!previousNetwork.TryGetValue(adapter.Key, out before)) continue;
                down += Math.Max(0, adapter.Value[0] - before[0]);
                up += Math.Max(0, adapter.Value[1] - before[1]);
            }
            double seconds = (now - previousNetworkTime) / (double)Stopwatch.Frequency;
            result.Download = down / seconds;
            result.Upload = up / seconds;
        }
        previousNetwork = current;
        previousNetworkTime = now;
    }

    public void Dispose() { gpu.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}

internal sealed class GpuSampler : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private IntPtr query, counter;
    private bool sampled;

    internal double? Read()
    {
        if (query == IntPtr.Zero)
        {
            IntPtr opened;
            if (PdhOpenQuery(null, IntPtr.Zero, out opened) != 0) return null;
            if (PdhAddEnglishCounter(opened, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0)
            {
                PdhCloseQuery(opened);
                return null;
            }
            query = opened;
        }

        if (PdhCollectQueryData(query) != 0) return null;
        if (!sampled) { sampled = true; return null; }

        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArray(counter, 0x00008200, ref size, ref count, IntPtr.Zero) != MoreData ||
            size == 0 || size > 16 * 1024 * 1024) return null;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, 0x00008200, ref size, ref count, buffer) != 0) return null;
            int stride = Marshal.SizeOf(typeof(CounterItem));
            var engines = new Dictionary<string, double>();
            for (uint i = 0; i < count && (i + 1) * stride <= size; i++)
            {
                var item = (CounterItem)Marshal.PtrToStructure(IntPtr.Add(buffer, (int)i * stride), typeof(CounterItem));
                if (item.Status > 1 || double.IsNaN(item.Value) || double.IsInfinity(item.Value) || item.Value < 0) continue;
                string instance = Marshal.PtrToStringUni(item.Name);
                int hardware = instance == null ? -1 : instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
                if (hardware < 0) continue;
                string engine = instance.Substring(hardware).Split('#')[0];
                double existing;
                engines.TryGetValue(engine, out existing);
                engines[engine] = existing + item.Value;
            }
            // Task Manager-style summary: use the busiest hardware engine, not a sum across engines.
            return engines.Count == 0 ? (double?)null : Math.Min(100, engines.Values.Max());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero) PdhCloseQuery(query);
        query = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct CounterItem
    {
        [FieldOffset(0)] public IntPtr Name;
        [FieldOffset(8)] public uint Status;
        [FieldOffset(16)] public double Value;
    }

    [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, ref uint count, IntPtr items);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed class ResourcesModule : IDisposable
{
    private readonly Func<StripSettings> settings;
    private readonly Action dismissBattery, openSettings;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1200 };
    private readonly ResourceSampler sampler = new ResourceSampler();
    private SystemTrayIcon tray;
    private Icon icon;
    private string iconKey;
    private ResourcePanel panel;
    private ResourceReading reading = new ResourceReading();
    private bool sampling, disposed;

    internal ResourcesModule(Func<StripSettings> settings, Action dismissBattery, Action openSettings)
    {
        this.settings = settings;
        this.dismissBattery = dismissBattery;
        this.openSettings = openSettings;
        timer.Tick += async (sender, args) => await Sample();
    }

    internal void Apply()
    {
        if (disposed) return;
        var current = settings();
        if (!current.ResourcesEnabled)
        {
            Close();
            timer.Stop();
            if (tray != null) { tray.Dispose(); tray = null; }
            if (icon != null) { icon.Dispose(); icon = null; }
            iconKey = null;
            return;
        }
        if (tray == null)
        {
            tray = new SystemTrayIcon(2, "Charge Strip PC Resources");
            tray.Activated += Toggle;
            tray.MenuRequested += () => { Close(); openSettings(); };
            timer.Start();
        }
        UpdateIcon(current);
        if (panel != null) panel.UpdateReading(reading, current);
    }

    private async Task Sample()
    {
        if (disposed || tray == null || sampling) return;
        sampling = true;
        try
        {
            reading = await Task.Run(() => sampler.Read());
            if (!disposed && tray != null)
            {
                UpdateIcon(settings());
                if (panel != null) panel.UpdateReading(reading, settings());
            }
        }
        finally
        {
            sampling = false;
            if (disposed) sampler.Dispose();
        }
    }

    private void UpdateIcon(StripSettings current)
    {
        string cpu = ResourcePanel.Percent(reading.Cpu);
        Color accent = StripSettings.ColorOf(current.Icons);
        string key = current.ResourcesIconOnly + ":" + accent.ToArgb() + ":" + (current.ResourcesIconOnly ? "" : cpu);
        Icon previous = null;
        if (key != iconKey)
        {
            previous = icon;
            icon = CreateIcon(reading.Cpu, accent, current.ResourcesIconOnly);
            iconKey = key;
        }
        tray.Update(icon, Localize.Text(current.Language, "Ресурси ПК") + " · CPU " + cpu);
        if (previous != null) previous.Dispose();
    }

    private static Icon CreateIcon(double? cpu, Color accent, bool iconOnly)
    {
        string number = cpu.HasValue ? Math.Round(cpu.Value).ToString() : "—";
        using (var bitmap = new Bitmap(32, 32))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var pen = new Pen(accent, 2))
        using (var fill = new SolidBrush(accent))
        using (var font = new Font("Segoe UI", number.Length > 2 ? 14 : 18, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            if (!iconOnly)
            {
                graphics.DrawRectangle(pen, 3, 5, 26, 22);
                graphics.DrawLine(pen, 0, 11, 3, 11);
                graphics.DrawLine(pen, 0, 21, 3, 21);
                graphics.DrawLine(pen, 29, 11, 32, 11);
                graphics.DrawLine(pen, 29, 21, 32, 21);
                graphics.DrawString(number, font, fill, new RectangleF(3, 4, 26, 24), format);
            }
            else DrawChip(graphics, new RectangleF(0, 0, 32, 32), accent);
            IntPtr handle = bitmap.GetHicon();
            try { using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
            finally { Native.DestroyIcon(handle); }
        }
    }

    internal static void DrawChip(Graphics graphics, RectangleF bounds, Color accent)
    {
        var state = graphics.Save();
        graphics.TranslateTransform(bounds.X, bounds.Y);
        graphics.ScaleTransform(bounds.Width / 32f, bounds.Height / 32f);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var outline = new Pen(accent, 1.7f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            graphics.DrawRectangle(outline, 6, 6, 20, 20);
            foreach (int pin in new[] { 11, 16, 21 })
            {
                graphics.DrawLine(outline, pin, 2, pin, 6);
                graphics.DrawLine(outline, pin, 26, pin, 30);
                graphics.DrawLine(outline, 2, pin, 6, pin);
                graphics.DrawLine(outline, 26, pin, 30, pin);
            }
            graphics.DrawLines(outline, new[] { new Point(9, 17), new Point(13, 17), new Point(15, 11),
                new Point(18, 22), new Point(20, 17), new Point(23, 17) });
        }
        graphics.Restore(state);
    }

    private void Toggle()
    {
        if (panel != null) { Close(); return; }
        dismissBattery();
        var bounds = tray.Bounds;
        if (bounds.IsEmpty) bounds = new Rectangle(Cursor.Position, new Size(1, 1));
        panel = new ResourcePanel(reading, settings(), bounds);
        panel.FormClosed += (sender, args) => { if (ReferenceEquals(panel, sender)) panel = null; };
        tray.PrepareMenu();
        panel.Show();
        if (panel != null) panel.Activate();
    }

    internal void Close()
    {
        if (panel == null) return;
        var current = panel;
        panel = null;
        current.Close();
    }

    public void Dispose()
    {
        disposed = true;
        timer.Dispose();
        Close();
        if (tray != null) tray.Dispose();
        if (icon != null) icon.Dispose();
        if (!sampling) sampler.Dispose();
    }
}

internal sealed class ResourcePanel : Form
{
    private readonly Rectangle anchor;
    private ResourceReading reading;
    private StripSettings settings;
    private readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };
    private readonly Stopwatch opening = new Stopwatch();
    private bool active;

    internal ResourcePanel(ResourceReading initial, StripSettings preferences, Rectangle anchor)
    {
        this.anchor = anchor;
        Text = Localize.Text(preferences.Language, "Ресурси ПК");
        AccessibleName = Text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        StartPosition = FormStartPosition.Manual;
        Opacity = .62;
        UpdateReading(initial, preferences);
        Place(96);
        fade.Tick += (sender, args) =>
        {
            double progress = Math.Min(1, opening.Elapsed.TotalMilliseconds / 110);
            Opacity = .62 + .38 * progress;
            if (progress >= 1) fade.Stop();
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Place(DeviceDpi);
        Native.RoundCorners(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        opening.Start();
        fade.Start();
        active = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (active) Close();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void Place(int dpi)
    {
        Rectangle screen = Screen.FromRectangle(anchor).WorkingArea;
        double scale = settings.Zoom / 100.0 * dpi / 96.0;
        int width = Math.Min(screen.Width, (int)(302 * scale));
        int height = Math.Min(screen.Height, (int)(240 * scale));
        int left = StripSettings.Clamp(anchor.Right - width, screen.Left, screen.Right - width);
        int top = StripSettings.Clamp(anchor.Top - height - 7, screen.Top, screen.Bottom - height);
        Bounds = new Rectangle(left, top, width, height);
    }

    internal void UpdateReading(ResourceReading value, StripSettings current)
    {
        reading = value;
        settings = current;
        BackColor = StripSettings.PopupColor(current.Background, current.Alpha);
        AccessibleDescription = string.Join(", ", Labels().Zip(Values(), (label, number) => label + " " + number));
        Invalidate();
    }

    private string[] Labels()
    {
        return new[] { "CPU", "RAM", "GPU", Localize.Text(settings.Language, "Системний диск"),
            "↓ " + Localize.Text(settings.Language, "Завантаження"), "↑ " + Localize.Text(settings.Language, "Віддача") };
    }

    private string[] Values()
    {
        return new[] { Percent(reading.Cpu), Memory(), Percent(reading.Gpu), Percent(reading.Disk),
            Rate(reading.Download), Rate(reading.Upload) };
    }

    internal static string Percent(double? value)
    {
        return value.HasValue ? value.Value.ToString("0") + "%" : "—";
    }

    private string Memory()
    {
        const double gib = 1024.0 * 1024 * 1024;
        if (!reading.MemoryUsed.HasValue || !reading.MemoryTotal.HasValue) return "—";
        return (reading.MemoryUsed.Value / gib).ToString("0.0") + " / " +
               (reading.MemoryTotal.Value / gib).ToString("0.0") + " GiB";
    }

    private static string Rate(double? value)
    {
        if (!value.HasValue) return "—";
        if (value.Value >= 1024 * 1024) return (value.Value / (1024 * 1024)).ToString("0.0") + " MiB/s";
        return (value.Value / 1024).ToString("0.0") + " KiB/s";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float scale = Math.Min(Width / 302f, Height / 240f);
        var graphics = e.Graphics;
        graphics.ScaleTransform(scale, scale);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Color ink = StripSettings.ColorOf(settings.Text);
        Color accent = StripSettings.ColorOf(settings.Icons);
        using (var text = new SolidBrush(ink))
        using (var rose = new SolidBrush(accent))
        using (var dim = new SolidBrush(Color.FromArgb(184, ink)))
        using (var line = new Pen(Color.FromArgb(60, accent)))
        using (var title = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var label = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var metric = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var end = new StringFormat { Alignment = StringAlignment.Far })
        {
            graphics.DrawRectangle(line, .5f, .5f, Width / scale - 1, Height / scale - 1);
            graphics.FillRectangle(rose, 16, 17, 3, 15);
            graphics.DrawString(Text, title, text, 26, 14);
            graphics.DrawLine(line, 16, 46, 286, 46);
            var keys = Labels();
            var numbers = Values();
            for (int i = 0; i < keys.Length; i++)
            {
                int y = 58 + i * 27;
                graphics.DrawString(keys[i], label, dim, 16, y);
                graphics.DrawString(numbers[i], metric, text, new RectangleF(136, y - 1, 150, 22), end);
                if (i == 3) graphics.DrawLine(line, 16, y + 23, 286, y + 23);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) fade.Dispose();
        base.Dispose(disposing);
    }
}
