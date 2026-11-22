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

[Flags]
public enum ResourceMetric { None = 0, Cpu = 1, Ram = 2, Gpu = 4, Disk = 8, Download = 16, Upload = 32 }

internal static class ResourceMetrics
{
    internal const ResourceMetric All = ResourceMetric.Cpu | ResourceMetric.Ram | ResourceMetric.Gpu |
        ResourceMetric.Disk | ResourceMetric.Download | ResourceMetric.Upload;
    internal static readonly ResourceMetric[] DefaultOrder = { ResourceMetric.Cpu, ResourceMetric.Ram, ResourceMetric.Gpu,
        ResourceMetric.Disk, ResourceMetric.Download, ResourceMetric.Upload };

    internal static ResourceMetric[] Normalize(ResourceMetric[] order)
    {
        return (order ?? DefaultOrder).Where(DefaultOrder.Contains).Concat(DefaultOrder).Distinct().ToArray();
    }

    internal static string Label(ResourceMetric metric)
    {
        switch (metric)
        {
            case ResourceMetric.Cpu: return "CPU";
            case ResourceMetric.Ram: return "RAM";
            case ResourceMetric.Gpu: return "GPU";
            case ResourceMetric.Disk: return "Системний диск";
            case ResourceMetric.Download: return "Завантаження";
            default: return "Віддача";
        }
    }

    internal static string ShortLabel(ResourceMetric metric, string language)
    {
        if (metric == ResourceMetric.Download) return "↓";
        if (metric == ResourceMetric.Upload) return "↑";
        return Localize.Text(language, metric == ResourceMetric.Disk ? "Диск" : Label(metric));
    }
}

internal sealed class ResourceReading
{
    internal double? Cpu, Gpu, Disk, Download, Upload;
    internal double NetworkCapacity;
    internal ulong? MemoryUsed, MemoryTotal;

    internal double? Percent(ResourceMetric metric)
    {
        switch (metric)
        {
            case ResourceMetric.Cpu: return Cpu;
            case ResourceMetric.Gpu: return Gpu;
            case ResourceMetric.Disk: return Disk;
            case ResourceMetric.Ram: return MemoryTotal.GetValueOrDefault() > 0 && MemoryUsed.HasValue
                ? 100.0 * MemoryUsed.Value / MemoryTotal.Value : (double?)null;
            default: return null;
        }
    }

    internal double? Rate(ResourceMetric metric)
    {
        return metric == ResourceMetric.Download ? Download : Upload;
    }

    internal string Value(ResourceMetric metric, bool compact)
    {
        if (metric == ResourceMetric.Download || metric == ResourceMetric.Upload)
        {
            double? rate = Rate(metric);
            if (!rate.HasValue) return "—";
            const double kib = 1024;
            bool large = rate >= kib * kib;
            double speed = rate.Value / (large ? kib * kib : kib);
            return compact ? speed.ToString("0.#") + (large ? "M" : "K")
                : speed.ToString("0.0") + (large ? " MiB/s" : " KiB/s");
        }
        if (metric == ResourceMetric.Ram && !compact && MemoryUsed.HasValue && MemoryTotal.HasValue)
        {
            const double gib = 1024.0 * 1024 * 1024;
            return (MemoryUsed.Value / gib).ToString("0.0") + " / " + (MemoryTotal.Value / gib).ToString("0.0") + " GiB";
        }
        double? percent = Percent(metric);
        return percent.HasValue ? percent.Value.ToString("0") + "%" : "—";
    }

    internal string Describe(IEnumerable<ResourceMetric> metrics, string language)
    {
        return string.Join(", ", metrics.Select(metric => Localize.Text(language, ResourceMetrics.Label(metric)) + " " + Value(metric, false)));
    }
}

internal sealed class ResourceSampler : IDisposable
{
    private ulong previousIdle, previousKernel, previousUser;
    private bool hasCpu;
    private long previousNetworkTime;
    private Dictionary<string, long[]> previousNetwork = new Dictionary<string, long[]>();
    private readonly GpuSampler gpu = new GpuSampler();

    private ResourceMetric lastMetrics;

    internal ResourceReading Read(ResourceMetric metrics)
    {
        var result = new ResourceReading();
        ulong idle, kernel, user;
        if ((metrics & ResourceMetric.Cpu) == 0) hasCpu = false;
        else if (GetSystemTimes(out idle, out kernel, out user))
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

        if ((metrics & ResourceMetric.Ram) != 0)
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical >= memory.AvailablePhysical)
            {
                result.MemoryTotal = memory.TotalPhysical;
                result.MemoryUsed = memory.TotalPhysical - memory.AvailablePhysical;
            }
        }

        if ((metrics & ResourceMetric.Disk) != 0)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                long total = drive.TotalSize;
                if (total > 0) result.Disk = 100 - 100.0 * drive.TotalFreeSpace / total;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var network = metrics & (ResourceMetric.Download | ResourceMetric.Upload);
        if (network != 0) ReadNetwork(result, network);
        else { previousNetwork.Clear(); previousNetworkTime = 0; }
        if ((metrics & ResourceMetric.Gpu) != 0) result.Gpu = gpu.Read();
        else if ((lastMetrics & ResourceMetric.Gpu) != 0) gpu.Dispose();
        lastMetrics = metrics;
        return result;
    }

    private void ReadNetwork(ResourceReading result, ResourceMetric network)
    {
        bool download = (network & ResourceMetric.Download) != 0;
        bool upload = (network & ResourceMetric.Upload) != 0;
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
                    current[adapter.Id] = new[] { download ? stats.BytesReceived : 0, upload ? stats.BytesSent : 0 };
                    result.NetworkCapacity += Math.Max(0, adapter.Speed) / 8.0;
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
            if (download && (lastMetrics & ResourceMetric.Download) != 0) result.Download = down / seconds;
            if (upload && (lastMetrics & ResourceMetric.Upload) != 0) result.Upload = up / seconds;
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
        query = counter = IntPtr.Zero;
        sampled = false;
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
    private readonly Action dismissBattery;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1200 };
    private readonly ResourceSampler sampler = new ResourceSampler();
    private ResourcePanel panel;
    private ResourceReading reading = new ResourceReading();
    private bool sampling, disposed;
    private ResourceMetric wanted;
    internal ResourceReading Reading { get { return reading; } }
    internal event Action Changed;

    internal ResourcesModule(Func<StripSettings> settings, Action dismissBattery)
    {
        this.settings = settings;
        this.dismissBattery = dismissBattery;
        timer.Tick += async (sender, args) => await Sample();
    }

    internal void Apply()
    {
        if (disposed) return;
        var current = settings();
        if (!current.ModuleVisible("resources"))
        {
            Close();
            SelectMetrics(ResourceMetric.None);
            return;
        }
        SelectMetrics(ResourceMetrics.All & ~(current.HiddenMetrics(true) & current.HiddenMetrics(false)));
        if (panel != null) panel.UpdateReading(reading, current);
    }

    private async void SelectMetrics(ResourceMetric metrics)
    {
        if (metrics == wanted) return;
        wanted = metrics;
        timer.Enabled = wanted != ResourceMetric.None;
        await Sample();
    }

    private async Task Sample()
    {
        if (disposed || sampling) return;
        var requested = wanted;
        sampling = true;
        try
        {
            var result = await Task.Run(() => sampler.Read(requested));
            if (!disposed && requested == wanted)
            {
                reading = result;
                var current = settings();
                if (panel != null) panel.UpdateReading(reading, current);
                if (Changed != null) Changed();
            }
        }
        finally
        {
            sampling = false;
            if (disposed) sampler.Dispose();
        }
        if (!disposed && requested != wanted) await Sample();
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

    internal static float TileWidth(int count, bool compact)
    {
        if (count == 0) return compact ? 24 : 29;
        return 26 + (count + 1) / 2 * (compact ? 36 : 51);
    }

    internal static void DrawTile(Graphics graphics, RectangleF bounds, ResourceReading reading, StripSettings settings)
    {
        var metrics = settings.VisibleMetrics(true).ToArray();
        Color accent = StripSettings.ColorOf(settings.Icons);
        float chipX = metrics.Length == 0 ? bounds.X + (bounds.Width - 24) / 2 : bounds.X + 1;
        DrawChip(graphics, new RectangleF(chipX, bounds.Y + (bounds.Height - 24) / 2, 24, 24), accent);
        if (metrics.Length == 0) return;
        using (var font = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(StripSettings.ColorOf(settings.Text)))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap })
        {
            int columns = (metrics.Length + 1) / 2;
            float width = (bounds.Width - 26) / columns;
            float height = metrics.Length > 1 ? bounds.Height / 2 : bounds.Height;
            for (int i = 0; i < metrics.Length; i++)
            {
                ResourceMetric metric = metrics[i];
                int column = i % columns, row = i / columns;
                string caption = ResourceMetrics.ShortLabel(metric, settings.Language) + " " + reading.Value(metric, true);
                graphics.DrawString(caption, font, ink, new RectangleF(bounds.X + 26 + column * width, bounds.Y + row * height,
                    width, height), format);
            }
        }
    }

    internal void Open(Rectangle bounds)
    {
        if (panel != null) { Close(); return; }
        dismissBattery();
        if (bounds.IsEmpty) bounds = new Rectangle(Cursor.Position, new Size(1, 1));
        panel = new ResourcePanel(reading, settings(), bounds);
        panel.FormClosed += (sender, args) => { if (ReferenceEquals(panel, sender)) panel = null; };
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
        int height = Math.Min(screen.Height, (int)(ViewHeight(settings) * scale));
        int left = StripSettings.Clamp(anchor.Right - width, screen.Left, screen.Right - width);
        int top = StripSettings.Clamp(anchor.Top - height - 7, screen.Top, screen.Bottom - height);
        Bounds = new Rectangle(left, top, width, height);
    }

    internal void UpdateReading(ResourceReading value, StripSettings current)
    {
        bool resize = settings != null && (settings.Zoom != current.Zoom || Height !=
            Math.Min(Screen.FromRectangle(anchor).WorkingArea.Height, (int)(ViewHeight(current) * current.Zoom / 100.0 * DeviceDpi / 96.0)));
        reading = value;
        settings = current;
        BackColor = StripSettings.PopupColor(current.Background, current.Alpha);
        AccessibleDescription = value.Describe(current.VisibleMetrics(false), current.Language);
        if (resize && IsHandleCreated) Place(DeviceDpi);
        Invalidate();
    }

    private static int ViewHeight(StripSettings settings)
    {
        return 58 + Math.Max(1, settings.VisibleMetrics(false).Count()) * (settings.ResourceScales ? 36 : 27);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Draw(e.Graphics, ClientRectangle, reading, settings);
    }

    internal static void Draw(Graphics graphics, Rectangle bounds, ResourceReading reading, StripSettings settings)
    {
        const int width = 302;
        int height = ViewHeight(settings);
        float scale = Math.Min(Math.Min(bounds.Width / (float)width, bounds.Height / (float)height), settings.Zoom / 100f *
            graphics.DpiX / 96);
        if (scale <= 0) return;
        var state = graphics.Save();
        graphics.TranslateTransform(bounds.X, bounds.Y);
        graphics.ScaleTransform(scale, scale);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        Color ink = StripSettings.ColorOf(settings.Text);
        Color accent = StripSettings.ColorOf(settings.Icons);
        using (var text = new SolidBrush(ink))
        using (var rose = new SolidBrush(accent))
        using (var dim = new SolidBrush(Color.FromArgb(184, ink)))
        using (var body = new SolidBrush(StripSettings.PopupColor(settings.Background, settings.Alpha)))
        using (var rail = new SolidBrush(Color.FromArgb(45, settings.ScaleColor)))
        using (var bar = new SolidBrush(settings.ScaleColor))
        using (var line = new Pen(Color.FromArgb(60, accent)))
        using (var title = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var label = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var metric = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var end = new StringFormat { Alignment = StringAlignment.Far })
        {
            graphics.FillRectangle(body, 0, 0, width, height);
            graphics.DrawRectangle(line, .5f, .5f, width - 1, height - 1);
            graphics.FillRectangle(rose, 16, 17, 3, 15);
            graphics.DrawString(Localize.Text(settings.Language, "Ресурси ПК"), title, text, 26, 14);
            graphics.DrawLine(line, 16, 46, 286, 46);
            var metrics = settings.VisibleMetrics(false).ToArray();
            if (metrics.Length == 0)
                graphics.DrawString(Localize.Text(settings.Language, "Показники не вибрано"), label, dim, 16, 58);
            for (int i = 0; i < metrics.Length; i++)
            {
                ResourceMetric key = metrics[i];
                int y = 56 + i * (settings.ResourceScales ? 36 : 27);
                graphics.DrawString(Localize.Text(settings.Language, ResourceMetrics.Label(key)), label, dim, 16, y);
                graphics.DrawString(reading.Value(key, false), metric, text, new RectangleF(136, y - 1, 150, 22), end);
                if (!settings.ResourceScales) continue;
                double? portion = reading.Percent(key) / 100;
                if (reading.NetworkCapacity > 0 && (key == ResourceMetric.Download || key == ResourceMetric.Upload))
                    portion = reading.Rate(key) / reading.NetworkCapacity;
                graphics.FillRectangle(rail, 16, y + 21, 270, 4);
                if (portion > 0) graphics.FillRectangle(bar, 16, y + 21, (float)(270 * Math.Min(1, portion.Value)), 4);
            }
        }
        graphics.Restore(state);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) fade.Dispose();
        base.Dispose(disposing);
    }
}
