using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Keep WinRT projection calls on one STA, while leaving the UI and its click path free.
internal sealed class MediaService : IDisposable
{
    private readonly Thread worker;
    private volatile Control dispatcher;
    private volatile bool disposed;
    private volatile MediaSnapshot current = new MediaSnapshot();
    private object manager, session;
    private string selectedSource, requestedArtwork;
    private DateTime retryAfter;
    private System.Windows.Forms.Timer timer;
    private bool busy;
    private int artworkGeneration;

    internal MediaSnapshot Current { get { return current; } }

    internal MediaService()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "Charge Strip Windows media sessions" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    private void Run()
    {
        using (var window = new Control())
        using (timer = new System.Windows.Forms.Timer { Interval = 1000 })
        {
            IntPtr handle = window.Handle;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Forms.WindowsFormsSynchronizationContext());
            dispatcher = window;
            timer.Tick += async (sender, args) => await Poll();
            if (disposed) return;
            timer.Start();
            Application.Run();
            dispatcher = null;
        }
    }

    private void Post(Action action)
    {
        Control window = dispatcher;
        if (disposed || window == null) return;
        try { window.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    internal void Select(string source)
    {
        Post(async () => { selectedSource = source; await Poll(); });
    }

    internal void Command(string action, string source, TimeSpan position)
    {
        Post(async () =>
        {
            object target = session;
            if (target == null || current.Source != source) return;
            try
            {
                var controls = WinMedia.Controls(target);
                var status = WinMedia.Status(target);
                string method;
                string capability;
                switch (action)
                {
                    case "previous": method = "TrySkipPreviousAsync"; capability = "IsPreviousEnabled"; break;
                    case "next": method = "TrySkipNextAsync"; capability = "IsNextEnabled"; break;
                    case "seek": method = "TryChangePlaybackPositionAsync"; capability = "IsPlaybackPositionEnabled"; break;
                    default:
                        bool playing = status == "Playing";
                        capability = playing ? "IsPauseEnabled" : "IsPlayEnabled";
                        method = playing ? "TryPauseAsync" : "TryPlayAsync";
                        if (!WinMedia.Enabled(controls, capability))
                        {
                            capability = "IsPlayPauseToggleEnabled";
                            method = "TryTogglePlayPauseAsync";
                        }
                        break;
                }
                if (!WinMedia.Enabled(controls, capability)) return;
                if (action == "seek")
                {
                    if (current.End <= current.Start) return;
                    long ticks = Math.Max(current.Start.Ticks, Math.Min(current.End.Ticks, position.Ticks));
                    await WinMedia.Operation(WinMedia.Call(WinMedia.Session, target, method, ticks), typeof(bool));
                }
                else await WinMedia.Operation(WinMedia.Call(WinMedia.Session, target, method), typeof(bool));
                await Poll();
            }
            catch (Exception ex) { if (!WinMedia.IsUnavailable(ex)) throw; }
        });
    }

    private async Task Poll()
    {
        if (busy || disposed || DateTime.UtcNow < retryAfter) return;
        busy = true;
        try
        {
            if (manager == null)
                manager = await WinMedia.Operation(WinMedia.Call(WinMedia.Manager, null, "RequestAsync"), WinMedia.Manager);
            if (disposed) return;
            var all = ((IEnumerable)WinMedia.Call(WinMedia.Manager, manager, "GetSessions")).Cast<object>().ToArray();
            var preferred = WinMedia.Call(WinMedia.Manager, manager, "GetCurrentSession");
            var eligible = all.Where(s => { string status = WinMedia.Status(s); return status == "Playing" || status == "Paused"; }).ToArray();
            string preferredSource = preferred == null ? null : WinMedia.Source(preferred);
            session = eligible.FirstOrDefault(s => WinMedia.Source(s) == selectedSource)
                ?? eligible.FirstOrDefault(s => WinMedia.Source(s) == preferredSource)
                ?? eligible.FirstOrDefault(s => WinMedia.Status(s) == "Playing")
                ?? eligible.FirstOrDefault();

            string[] sources = eligible.Select(WinMedia.Source).Distinct().ToArray();
            if (selectedSource != null && !sources.Contains(selectedSource)) selectedSource = null;
            if (session == null)
            {
                requestedArtwork = null;
                artworkGeneration++;
                current = new MediaSnapshot();
                return;
            }

            string source = WinMedia.Source(session);
            bool playing = WinMedia.Status(session) == "Playing";
            object controls = WinMedia.Controls(session);
            object timeline = WinMedia.Call(WinMedia.Session, session, "GetTimelineProperties");
            object media = await WinMedia.Operation(WinMedia.Call(WinMedia.Session, session, "TryGetMediaPropertiesAsync"), WinMedia.Properties);
            if (disposed || media == null) return;
            string title = WinMedia.Read<string>(WinMedia.Properties, media, "Title");
            string artist = WinMedia.Read<string>(WinMedia.Properties, media, "Artist");
            if (string.IsNullOrWhiteSpace(artist)) artist = WinMedia.Read<string>(WinMedia.Properties, media, "AlbumArtist");
            string artworkKey = source + "\n" + title + "\n" + artist + "\n" + WinMedia.Read<string>(WinMedia.Properties, media, "AlbumTitle");
            var snapshot = new MediaSnapshot
            {
                Source = source, Sources = sources, Title = title, Artist = artist, Playing = playing, ArtworkKey = artworkKey,
                Artwork = current.ArtworkKey == artworkKey ? current.Artwork : null,
                Start = WinMedia.Read<TimeSpan>(WinMedia.Timeline, timeline, "StartTime"),
                End = WinMedia.Read<TimeSpan>(WinMedia.Timeline, timeline, "EndTime"),
                Position = WinMedia.Read<TimeSpan>(WinMedia.Timeline, timeline, "Position"),
                Updated = WinMedia.Read<DateTimeOffset>(WinMedia.Timeline, timeline, "LastUpdatedTime"),
                CanPrevious = WinMedia.Enabled(controls, "IsPreviousEnabled"),
                CanNext = WinMedia.Enabled(controls, "IsNextEnabled"),
                CanPlayPause = WinMedia.Enabled(controls, playing ? "IsPauseEnabled" : "IsPlayEnabled")
                    || WinMedia.Enabled(controls, "IsPlayPauseToggleEnabled"),
                CanSeek = WinMedia.Enabled(controls, "IsPlaybackPositionEnabled")
            };
            current = snapshot;
            var thumbnail = WinMedia.Read<object>(WinMedia.Properties, media, "Thumbnail");
            if (thumbnail != null && requestedArtwork != artworkKey)
            {
                requestedArtwork = artworkKey;
                LoadArtwork(thumbnail, artworkKey, ++artworkGeneration);
            }
        }
        catch (Exception ex)
        {
            if (!WinMedia.IsUnavailable(ex)) throw;
            manager = null;
            retryAfter = DateTime.UtcNow.AddSeconds(4);
        }
        finally { busy = false; }
    }

    private async void LoadArtwork(object reference, string key, int generation)
    {
        try
        {
            object stream = await WinMedia.Operation(WinMedia.Call(WinMedia.StreamReference, reference, "OpenReadAsync"), WinMedia.RandomStream);
            byte[] png = null;
            using (var input = WinMedia.AsStream(stream))
            {
                if (input.Length > 0 && input.Length <= 8 * 1024 * 1024)
                {
                    int length = (int)input.Length;
                    byte[] bytes = new byte[length];
                    int offset = 0;
                    while (offset < length)
                    {
                        int read = await input.ReadAsync(bytes, offset, length - offset);
                        if (read == 0) break;
                        offset += read;
                    }
                    if (offset == length) png = await Task.Run(() => DecodeArtwork(bytes));
                }
            }
            if (!disposed && generation == artworkGeneration && current.ArtworkKey == key)
                current = current.WithArtwork(png);
        }
        catch (Exception ex)
        {
            if (!WinMedia.IsUnavailable(ex) && !(ex is ArgumentException) && !(ex is IOException) && !(ex is OutOfMemoryException))
                throw;
        }
    }

    private static byte[] DecodeArtwork(byte[] data)
    {
        using (var input = new MemoryStream(data))
        using (var image = Image.FromStream(input))
        {
            double scale = Math.Min(1, 480.0 / Math.Max(image.Width, image.Height));
            using (var thumb = new Bitmap(image, Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale))))
            using (var output = new MemoryStream())
            {
                thumb.Save(output, ImageFormat.Png);
                return output.ToArray();
            }
        }
    }

    public void Dispose()
    {
        Post(() => { timer.Stop(); Application.ExitThread(); });
        disposed = true;
    }

}

internal sealed class MediaSnapshot
{
    internal string Source, Title, Artist, ArtworkKey;
    internal string[] Sources = new string[0];
    internal byte[] Artwork;
    internal TimeSpan Start, End, Position;
    internal DateTimeOffset Updated;
    internal bool Playing, CanPrevious, CanNext, CanPlayPause, CanSeek;

    internal MediaSnapshot WithArtwork(byte[] data)
    {
        var copy = (MediaSnapshot)MemberwiseClone();
        copy.Artwork = data;
        return copy;
    }

    internal TimeSpan CurrentPosition
    {
        get
        {
            double elapsed = Playing && Updated != default(DateTimeOffset) ? Math.Max(0, (DateTimeOffset.UtcNow - Updated).TotalSeconds) : 0;
            double upper = End > Start ? End.TotalSeconds : double.MaxValue;
            return TimeSpan.FromSeconds(Math.Min(upper, Math.Max(Start.TotalSeconds, Position.TotalSeconds + Math.Min(3600, elapsed))));
        }
    }

    internal static string SourceLabel(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (id.IndexOf("Spotify", StringComparison.OrdinalIgnoreCase) >= 0) return "Spotify";
        if (id.IndexOf("Chrome", StringComparison.OrdinalIgnoreCase) >= 0) return "Chrome";
        if (id.IndexOf("MSEdge", StringComparison.OrdinalIgnoreCase) >= 0) return "Edge";
        if (id.IndexOf("Firefox", StringComparison.OrdinalIgnoreCase) >= 0) return "Firefox";
        return id;
    }
}

// .NET Framework runtime projection. This also builds with the in-box compiler, without an SDK.
internal static class WinMedia
{
    private const string Prefix = "Windows.Media.Control.GlobalSystemMediaTransportControlsSession";
    private static readonly Assembly Bridge = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
    private static readonly MethodInfo AsTask = Bridge.GetType("System.WindowsRuntimeSystemExtensions").GetMethods()
        .First(method => method.Name == "AsTask" && method.IsGenericMethodDefinition &&
            method.GetGenericArguments().Length == 1 && method.GetParameters().Length == 1);
    private static readonly MethodInfo StreamReader = Bridge.GetType("System.IO.WindowsRuntimeStreamExtensions").GetMethods()
        .First(method => method.Name == "AsStreamForRead" && method.GetParameters().Length == 1);
    internal static readonly Type Manager = Project(Prefix + "Manager"), Session = Project(Prefix),
        Properties = Project(Prefix + "MediaProperties"), Timeline = Project(Prefix + "TimelineProperties"),
        Info = Project(Prefix + "PlaybackInfo"), PlaybackControls = Project(Prefix + "PlaybackControls"),
        StreamReference = Project("Windows.Storage.Streams.IRandomAccessStreamReference"),
        RandomStream = Project("Windows.Storage.Streams.IRandomAccessStreamWithContentType");

    private static Type Project(string name) { return Type.GetType(name + ", Windows, ContentType=WindowsRuntime", true); }
    internal static object Call(Type type, object instance, string method, params object[] args)
    {
        return type.GetMethod(method).Invoke(instance, args);
    }
    internal static T Read<T>(Type type, object value, string property)
    {
        return value == null ? default(T) : (T)type.GetProperty(property).GetValue(value, null);
    }
    internal static string Source(object session) { return Read<string>(Session, session, "SourceAppUserModelId"); }
    internal static string Status(object session)
    {
        object state = Read<object>(Info, Call(Session, session, "GetPlaybackInfo"), "PlaybackStatus");
        return state == null ? "" : state.ToString();
    }
    internal static object Controls(object session) { return Read<object>(Info, Call(Session, session, "GetPlaybackInfo"), "Controls"); }
    internal static bool Enabled(object controls, string property) { return Read<bool>(PlaybackControls, controls, property); }

    internal static async Task<object> Operation(object operation, Type result)
    {
        var task = (Task)AsTask.MakeGenericMethod(result).Invoke(null, new[] { operation });
        if (await Task.WhenAny(task, Task.Delay(3000)) != task) throw new TimeoutException();
        await task;
        return task.GetType().GetProperty("Result").GetValue(task, null);
    }

    internal static Stream AsStream(object stream) { return (Stream)StreamReader.Invoke(null, new[] { stream }); }

    internal static bool IsUnavailable(Exception error)
    {
        while (error is TargetInvocationException || error is TypeInitializationException)
        {
            if (error.InnerException == null) break;
            error = error.InnerException;
        }
        return error is COMException || error is UnauthorizedAccessException || error is TypeLoadException ||
               error is TimeoutException || error is OperationCanceledException ||
               (error.HResult & unchecked((int)0x80000000)) != 0 && error.GetType() == typeof(Exception);
    }
}
