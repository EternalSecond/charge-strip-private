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

internal sealed class ReadingsFlyout : Form
{
    private readonly Strip strip;
    private readonly int count, columns, logicalWidth, overviewHeight;
    private int logicalHeight;
    private readonly Point anchor;
    private readonly System.Windows.Forms.Timer dismiss = new System.Windows.Forms.Timer { Interval = 35 };
    private readonly System.Windows.Forms.Timer refreshRanking = new System.Windows.Forms.Timer { Interval = 1200 };
    private ProcessRanking ranking, sampling;
    private RankedProcess[] ranked;
    private bool escapeDown;
    internal bool Battery { get; private set; }

    internal ReadingsFlyout(Strip strip, bool battery, Point anchor)
    {
        this.strip = strip;
        this.anchor = anchor;
        Battery = battery;
        Text = battery ? "Batteries" : "PC Resources";
        count = battery ? strip.Readings.Count : strip.Current.VisibleMetrics(false).Count();
        columns = !battery && count > 1 ? 2 : 1;
        logicalWidth = battery ? 340 : 322;
        overviewHeight = logicalHeight = 54 + Math.Max(1, (count + columns - 1) / columns) * (battery ? 58 : 52);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(19, 23, 26);
        Place(96);
        refreshRanking.Tick += async (sender, args) => await UpdateRanking();
        dismiss.Tick += (sender, args) =>
        {
            bool down = (GetAsyncKeyState(27) & 0x8000) != 0;
            if (down && !escapeDown)
            {
                if (ranking != null) ShowOverview();
                else Close();
            }
            escapeDown = down;
            if ((((GetAsyncKeyState(1) | GetAsyncKeyState(2)) & 0x8000) != 0 &&
                !Bounds.Contains(Cursor.Position) && Math.Abs(Cursor.Position.X - anchor.X) > 18))
                Close();
        };
    }

    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ClassStyle |= 0x00020000; return p; }
    }

    private void Place(int dpi)
    {
        var work = Screen.FromPoint(anchor).WorkingArea;
        float scale = Math.Min(dpi / 96f, Math.Min(work.Width / (float)logicalWidth, work.Height / (float)logicalHeight));
        int width = (int)(logicalWidth * scale), height = (int)(logicalHeight * scale);
        Bounds = new Rectangle(StripSettings.Clamp(anchor.X - width / 2, work.Left, work.Right - width),
            StripSettings.Clamp(Math.Min(anchor.Y, work.Bottom) - height - 7, work.Top, work.Bottom - height), width, height);
        using (var outline = StripAppearance.Rounded(ClientRectangle, 22 * scale))
        {
            Region previous = Region;
            Region = new Region(outline);
            if (previous != null) previous.Dispose();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Place(DeviceDpi);
        dismiss.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.ScaleTransform(Width / (float)logicalWidth, Height / (float)logicalHeight);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var heading = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var label = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var value = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(Color.FromArgb(250, 245, 240)))
        using (var muted = new SolidBrush(Color.FromArgb(174, 185, 189)))
        using (var tileFill = new SolidBrush(Color.FromArgb(37, 43, 46)))
        using (var rail = new SolidBrush(Color.FromArgb(66, 75, 80)))
        using (var frame = new Pen(Color.FromArgb(58, 68, 73)))
        using (var outline = StripAppearance.Rounded(new RectangleF(.5f, .5f, logicalWidth - 1, logicalHeight - 1), 22))
        using (var truncate = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
        {
            g.DrawPath(frame, outline);
            if (ranking != null)
            {
                DrawRanking(g, heading, label, value, ink, muted, tileFill, rail, truncate);
                return;
            }
            g.DrawString(Text, heading, ink, 17, 14);
            var batteries = strip.Readings;
            var metrics = strip.Current.VisibleMetrics(false).ToArray();
            int visible = Math.Min(count, Battery ? batteries.Count : metrics.Length);
            if (visible == 0) g.DrawString("—", value, muted, 17, 44);
            int stride = Battery ? 58 : 52, width = (logicalWidth - 34 - (columns - 1) * 8) / columns;
            for (int i = 0; i < visible; ++i)
            {
                int x = 17 + i % columns * (width + 8), y = 40 + i / columns * stride;
                using (var tile = StripAppearance.Rounded(new RectangleF(x, y, width, stride - 7), 10))
                    g.FillPath(tileFill, tile);
                var metric = Battery ? ResourceMetric.None : metrics[i];
                string title = Localize.Text("en", Battery ? batteries[i].Name : ResourceMetrics.Label(metric));
                string number = Battery ? batteries[i].Level : strip.ResourceReading.Value(metric, false);
                double? level = Battery ? batteries[i].Percent : strip.ResourceReading.Percent(metric);
                if (!Battery && !level.HasValue && strip.ResourceReading.NetworkCapacity > 0 &&
                    (metric == ResourceMetric.Download || metric == ResourceMetric.Upload))
                    level = 100 * strip.ResourceReading.Rate(metric) / strip.ResourceReading.NetworkCapacity;
                using (var accent = new SolidBrush(Accent(metric, level)))
                {
                    g.FillEllipse(accent, x + 9, y + 10, 5, 5);
                    g.DrawString(title, label, muted, new RectangleF(x + 20, y + 4, width - 28, 16), truncate);
                    g.DrawString(number, value, ink, x + 18, y + 17);
                    if (Battery) g.DrawString(Localize.Text("en", batteries[i].Status), label, muted, x + 73, y + 21);
                    if (!level.HasValue) continue;
                    g.FillRectangle(rail, x + 10, y + stride - 15, width - 20, 3);
                    g.FillRectangle(accent, x + 10, y + stride - 15,
                        (float)((width - 20) * Math.Max(0, Math.Min(100, level.Value)) / 100), 3);
                }
            }
        }
    }

    protected override async void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (Battery || e.Button != MouseButtons.Left) return;
        float x = e.X * logicalWidth / (float)Width, y = e.Y * logicalHeight / (float)Height;
        if (ranking != null)
        {
            if (x < 82 && y < 41) ShowOverview();
            return;
        }
        var metrics = strip.Current.VisibleMetrics(false).Take(count).ToArray();
        int width = (logicalWidth - 34 - (columns - 1) * 8) / columns;
        for (int i = 0; i < metrics.Length; i++)
        {
            if (!new RectangleF(17 + i % columns * (width + 8), 40 + i / columns * 52, width, 45).Contains(x, y)) continue;
            ranking = new ProcessRanking(metrics[i]);
            ranked = null;
            logicalHeight = 294;
            Place(DeviceDpi);
            Invalidate();
            refreshRanking.Start();
            await UpdateRanking();
            return;
        }
    }

    private async Task UpdateRanking()
    {
        if (sampling != null || ranking == null || !ranking.Supported) return;
        var active = sampling = ranking;
        try
        {
            var data = await Task.Run(() => active.Read());
            if (ReferenceEquals(ranking, active) && !IsDisposed)
            {
                ranked = data;
                Invalidate();
            }
        }
        finally
        {
            sampling = null;
            if (!ReferenceEquals(ranking, active)) active.Dispose();
        }
    }

    private void ClearRanking()
    {
        refreshRanking.Stop();
        if (ranking != null && !ReferenceEquals(ranking, sampling)) ranking.Dispose();
        ranking = null;
    }

    private void ShowOverview()
    {
        ClearRanking();
        logicalHeight = overviewHeight;
        Place(DeviceDpi);
        Invalidate();
    }

    private void DrawRanking(Graphics g, Font heading, Font label, Font value, Brush ink, Brush muted, Brush tileFill,
        Brush rail, StringFormat truncate)
    {
        var metric = ranking.Metric;
        g.DrawString("‹ Back", label, ink, 18, 14);
        g.DrawString(Localize.Text("en", ResourceMetrics.Label(metric)), heading, ink, 84, 12);
        if (!ranking.Supported || ranked == null || ranked.Length == 0)
        {
            string message = !ranking.Supported || (ranked != null && ranked.Length == 0)
                ? "Per-process readings unavailable." : "Collecting process readings…";
            g.DrawString(message, label, muted, 18, 54);
            return;
        }
        using (var accent = new SolidBrush(Accent(metric, null)))
        using (var right = new StringFormat { Alignment = StringAlignment.Far })
        {
            double peak = ranked[0].Amount;
            for (int i = 0; i < ranked.Length; i++)
            {
                int y = 42 + i * 47;
                var process = ranked[i];
                using (var tile = StripAppearance.Rounded(new RectangleF(17, y, logicalWidth - 34, 40), 10))
                    g.FillPath(tileFill, tile);
                g.DrawString(process.Name, label, ink, new RectangleF(28, y + 8, 174, 19), truncate);
                g.DrawString(process.Format(metric), value, ink, new RectangleF(185, y + 5, logicalWidth - 211, 22), right);
                g.FillRectangle(rail, 28, y + 31, logicalWidth - 56, 3);
                if (peak > 0)
                    g.FillRectangle(accent, 28, y + 31, (float)((logicalWidth - 56) * process.Amount / peak), 3);
            }
        }
    }

    private static Color Accent(ResourceMetric metric, double? level)
    {
        switch (metric)
        {
            case ResourceMetric.Cpu: return Color.FromArgb(84, 200, 238);
            case ResourceMetric.Ram: return Color.FromArgb(245, 185, 66);
            case ResourceMetric.Gpu: return Color.FromArgb(239, 119, 115);
            case ResourceMetric.Disk: return Color.FromArgb(113, 210, 186);
            case ResourceMetric.Download: return Color.FromArgb(129, 172, 249);
            case ResourceMetric.Upload: return Color.FromArgb(186, 164, 236);
            default: return level <= 25 ? Color.FromArgb(239, 119, 115) : Color.FromArgb(121, 217, 191);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dismiss.Dispose();
            ClearRanking();
            refreshRanking.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}

[Flags]
public enum ResourceMetric { None = 0, Cpu = 1, Ram = 2, Gpu = 4, Disk = 8, Download = 16, Upload = 32 }

internal sealed class RankedProcess
{
    internal readonly string Name;
    internal readonly double Amount;

    internal RankedProcess(string name, int pid, double amount)
    {
        Name = name + " (" + pid + ")";
        Amount = amount;
    }

    internal string Format(ResourceMetric metric)
    {
        if (metric != ResourceMetric.Ram) return Amount < .1 ? "<0.1%" : Amount.ToString("0.0") + "%";
        return Amount >= 1073741824 ? (Amount / 1073741824).ToString("0.00") + " GiB" :
            (Amount / 1048576).ToString("0.0") + " MiB";
    }
}

internal sealed class ProcessRanking : IDisposable
{
    private readonly GpuSampler gpu = new GpuSampler();
    private Dictionary<int, Tuple<long, double>> previousCpu = new Dictionary<int, Tuple<long, double>>();
    private long previousTime;
    internal readonly ResourceMetric Metric;
    internal bool Supported { get { return Metric == ResourceMetric.Cpu || Metric == ResourceMetric.Ram || Metric == ResourceMetric.Gpu; } }

    internal ProcessRanking(ResourceMetric metric) { Metric = metric; }

    internal RankedProcess[] Read()
    {
        var results = new List<RankedProcess>();
        var cpu = new Dictionary<int, Tuple<long, double>>();
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - previousTime) / (double)Stopwatch.Frequency;
        bool warming = Metric == ResourceMetric.Cpu && previousTime == 0;
        var gpuProcesses = Metric == ResourceMetric.Gpu ? new Dictionary<int, double>() : null;
        if (gpuProcesses != null && !gpu.Read(gpuProcesses).HasValue)
            return gpu.Pending ? null : new RankedProcess[0];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    int pid = process.Id;
                    if (pid == 0) continue;
                    double amount;
                    if (Metric == ResourceMetric.Ram) amount = process.WorkingSet64;
                    else if (Metric == ResourceMetric.Cpu)
                    {
                        var snapshot = Tuple.Create(process.StartTime.Ticks, process.TotalProcessorTime.TotalSeconds);
                        cpu[pid] = snapshot;
                        Tuple<long, double> before;
                        if (!previousCpu.TryGetValue(pid, out before) || before.Item1 != snapshot.Item1 || elapsed <= 0)
                            continue;
                        amount = Math.Min(100, 100 * (snapshot.Item2 - before.Item2) / (elapsed * Environment.ProcessorCount));
                    }
                    else if (!gpuProcesses.TryGetValue(pid, out amount)) continue;
                    if (amount > 0) results.Add(new RankedProcess(process.ProcessName, pid, amount));
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
        previousCpu = cpu;
        previousTime = now;
        return warming ? null : results.OrderByDescending(process => process.Amount).Take(5).ToArray();
    }

    public void Dispose() { gpu.Dispose(); }
}

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
    internal bool Pending { get; private set; }

    internal double? Read(Dictionary<int, double> processes = null)
    {
        Pending = false;
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
        if (!sampled) { sampled = true; Pending = true; return null; }

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
                if (processes != null && instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
                {
                    int end = instance.IndexOf('_', 4), pid;
                    if (end > 4 && int.TryParse(instance.Substring(4, end - 4), out pid))
                    {
                        double highest;
                        processes.TryGetValue(pid, out highest);
                        processes[pid] = Math.Min(100, Math.Max(highest, item.Value));
                    }
                }
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
    private const int ChipSize = 20, ChipAndGap = 26;
    private readonly Func<StripSettings> settings;

    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1200 };
    private readonly ResourceSampler sampler = new ResourceSampler();
    private ResourceReading reading = new ResourceReading();
    private bool sampling, disposed;
    private ResourceMetric wanted;
    internal ResourceReading Reading { get { return reading; } }
    internal event Action Changed;

    internal ResourcesModule(Func<StripSettings> settings)
    {
        this.settings = settings;
        timer.Tick += async (sender, args) => await Sample();
    }

    internal void Apply()
    {
        if (disposed) return;
        var current = settings();
        if (!current.ModuleVisible("resources"))
        {
            SelectMetrics(ResourceMetric.None);
            return;
        }
        SelectMetrics(ResourceMetrics.All & ~(current.HiddenMetrics(true) & current.HiddenMetrics(false)));
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
        if (count == 0) return ChipSize;
        return ChipAndGap + (count + 1) / 2 * (compact ? 36 : 51);
    }

    internal static void DrawTile(Graphics graphics, RectangleF bounds, ResourceReading reading, StripSettings settings)
    {
        var metrics = settings.VisibleMetrics(true).ToArray();
        Color accent = StripSettings.ColorOf(settings.Icons);
        float chipX = metrics.Length == 0 ? bounds.X + (bounds.Width - ChipSize) / 2 : bounds.X;
        DrawChip(graphics, new RectangleF(chipX, bounds.Y + (bounds.Height - ChipSize) / 2, ChipSize, ChipSize), accent);
        if (metrics.Length == 0) return;
        using (var font = new Font("Segoe UI", 9, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(StripSettings.ColorOf(settings.Text)))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap })
        {
            int columns = (metrics.Length + 1) / 2;
            float width = (bounds.Width - ChipAndGap) / columns;
            float height = metrics.Length > 1 ? bounds.Height / 2 : bounds.Height;
            for (int i = 0; i < metrics.Length; i++)
            {
                ResourceMetric metric = metrics[i];
                int column = i % columns, row = i / columns;
                string caption = ResourceMetrics.ShortLabel(metric, settings.Language) + " " + reading.Value(metric, true);
                graphics.DrawString(caption, font, ink, new RectangleF(bounds.X + ChipAndGap + column * width, bounds.Y + row * height,
                    width, height), format);
            }
        }
    }

    public void Dispose()
    {
        disposed = true;
        timer.Dispose();
        if (!sampling) sampler.Dispose();
    }
}

internal static class ResourcePanel
{
    internal static int ViewHeight(StripSettings settings)
    {
        return 58 + Math.Max(1, settings.VisibleMetrics(false).Count()) * (settings.ResourceScales ? 36 : 27);
    }

    internal static void Draw(Graphics graphics, Rectangle bounds, ResourceReading reading, StripSettings settings, bool embedded = false)
    {
        const int width = 302;
        int height = ViewHeight(settings);
        float scale = Math.Min(Math.Min(bounds.Width / (float)width, bounds.Height / (float)height), settings.Zoom / 100f *
            graphics.DpiX / 96);
        if (scale <= 0) return;
        var state = graphics.Save();
        graphics.TranslateTransform(bounds.X + (bounds.Width - width * scale) / 2, bounds.Y);
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
            if (!embedded)
            {
                graphics.FillRectangle(body, 0, 0, width, height);
                graphics.DrawRectangle(line, .5f, .5f, width - 1, height - 1);
            }
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


}
