namespace G19Tidal;

/// <summary>
/// Immutable snapshot of what the media session is playing. Produced on the poller
/// thread, consumed on the render thread, so it deliberately holds no GDI+ objects -
/// artwork travels as raw encoded bytes and is decoded by the renderer.
/// </summary>
public sealed record NowPlaying
{
    public static readonly NowPlaying None = new()
    {
        Title = "", Artist = "", Album = "", Key = "", HasSession = false,
    };

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required string Key { get; init; }

    public bool HasSession { get; init; }
    public bool IsPlaying { get; init; }
    public string SourceApp { get; init; } = "";

    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>When this snapshot was taken.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the player last actually moved <see cref="Position"/>, from the SMTC timeline's
    /// own LastUpdatedTime.
    ///
    /// This must not be the poll time. Players are free to refresh their timeline rarely -
    /// TIDAL does - and anchoring to the poll instead resets the interpolated part on every
    /// poll, which freezes the elapsed time on screen at whatever the player last reported.
    /// </summary>
    public DateTimeOffset PositionAnchor { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Encoded (JPEG/PNG) cover art, or null when the session exposes none.</summary>
    public byte[]? Artwork { get; init; }

    /// <summary>
    /// Playback position extrapolated to now. SMTC only refreshes its timeline every
    /// second or so; without this the progress bar visibly stutters.
    /// </summary>
    public TimeSpan EffectivePosition
    {
        get
        {
            if (!IsPlaying) return Clamp(Position);
            var elapsed = DateTimeOffset.UtcNow - PositionAnchor;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            return Clamp(Position + elapsed);
        }
    }

    private TimeSpan Clamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero) return TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && value > Duration) return Duration;
        return value;
    }
}
