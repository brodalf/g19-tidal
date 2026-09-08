using Windows.Media.Control;
using Windows.Storage.Streams;

namespace G19Tidal;

/// <summary>
/// Polls the Windows System Media Transport Controls for the TIDAL session.
///
/// TIDAL is an Electron app with no local API of its own, but like every well-behaved
/// Windows media player it publishes to SMTC - which gives us title, artist, album,
/// cover art, timeline and transport controls for free.
///
/// Polling rather than event subscription is deliberate: the SMTC change events from
/// Electron hosts are unreliable (they fire late, or not at all after a reconnect),
/// and a 500 ms poll of an in-process broker call is cheap.
/// </summary>
public sealed class MediaWatcher
{
    private readonly bool _anySource;
    private volatile NowPlaying _current = NowPlaying.None;

    private string _artworkKey = "";
    private byte[]? _artworkBytes;

    public MediaWatcher(bool anySource) => _anySource = anySource;

    public NowPlaying Current => _current;

    // Written by the poller, read by the render thread when a transport button is pressed.
    private volatile GlobalSystemMediaTransportControlsSession? _session;

    public GlobalSystemMediaTransportControlsSession? Session
    {
        get => _session;
        private set => _session = value;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct);
                await PollOnceAsync(manager, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A session can vanish mid-call while TIDAL restarts. Drop the manager so
                // the next iteration re-acquires it, and keep the last good snapshot.
                manager = null;
                _current = NowPlaying.None;
            }

            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(GlobalSystemMediaTransportControlsSessionManager manager, CancellationToken ct)
    {
        var session = PickSession(manager);
        Session = session;

        if (session is null)
        {
            _current = NowPlaying.None;
            _artworkKey = "";
            _artworkBytes = null;
            return;
        }

        var props = await session.TryGetMediaPropertiesAsync().AsTask(ct);
        var timeline = session.GetTimelineProperties();
        var playback = session.GetPlaybackInfo();

        var title = props?.Title?.Trim() ?? "";
        var artist = props?.Artist?.Trim() ?? "";
        var album = props?.AlbumTitle?.Trim() ?? "";
        var key = $"{title}|{artist}|{album}";

        if (key != _artworkKey)
        {
            _artworkBytes = props is null ? null : await TryReadThumbnailAsync(props, ct);
            _artworkKey = key;
        }

        _current = new NowPlaying
        {
            Title = title,
            Artist = artist,
            Album = album,
            Key = key,
            HasSession = true,
            IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            SourceApp = session.SourceAppUserModelId,
            Position = timeline.Position,
            Duration = timeline.EndTime - timeline.StartTime,
            CapturedAt = DateTimeOffset.UtcNow,
            PositionAnchor = Anchor(timeline.LastUpdatedTime),
            Artwork = _artworkBytes,
        };
    }

    /// <summary>
    /// Falls back to now when the player leaves LastUpdatedTime unset, and refuses anchors in
    /// the future or absurdly far in the past - either would make the elapsed time jump.
    /// </summary>
    private static DateTimeOffset Anchor(DateTimeOffset lastUpdated)
    {
        var now = DateTimeOffset.UtcNow;
        if (lastUpdated == default || lastUpdated > now) return now;
        return now - lastUpdated > TimeSpan.FromHours(12) ? now : lastUpdated;
    }

    private GlobalSystemMediaTransportControlsSession? PickSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var sessions = manager.GetSessions();

        foreach (var s in sessions)
        {
            if (s.SourceAppUserModelId.Contains("tidal", StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return _anySource ? manager.GetCurrentSession() : null;
    }

    /// <summary>
    /// Reads the cover art through pure WinRT primitives. DataReader is used instead of
    /// the stream interop extensions so this stays independent of which interop shims
    /// the projection happens to expose.
    /// </summary>
    private static async Task<byte[]?> TryReadThumbnailAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties props, CancellationToken ct)
    {
        var reference = props.Thumbnail;
        if (reference is null) return null;

        try
        {
            using var stream = await reference.OpenReadAsync().AsTask(ct);
            var size = (uint)stream.Size;
            if (size == 0) return null;

            var buffer = new Windows.Storage.Streams.Buffer(size);
            await stream.ReadAsync(buffer, size, InputStreamOptions.None).AsTask(ct);

            var bytes = new byte[buffer.Length];
            using var reader = DataReader.FromBuffer(buffer);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public async Task TogglePlayPauseAsync()  => await SafeAsync(s => s.TryTogglePlayPauseAsync().AsTask());
    public async Task NextAsync()             => await SafeAsync(s => s.TrySkipNextAsync().AsTask());
    public async Task PreviousAsync()         => await SafeAsync(s => s.TrySkipPreviousAsync().AsTask());

    private async Task SafeAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        var session = Session;
        if (session is null) return;
        try { await action(session); } catch { /* transport control refused - nothing to do */ }
    }
}
