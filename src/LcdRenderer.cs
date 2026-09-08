using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace G19Tidal;

/// <summary>
/// Draws the 320x240 now-playing screen and hands it to the LCD.
///
/// The cover art is used twice: once blurred and dimmed as a full-bleed background, once
/// sharp as a thumbnail. Both derived bitmaps are cached per track, so a frame costs only
/// the composition, not the scaling.
/// </summary>
public sealed class LcdRenderer : IDisposable
{
    private const int W = LogitechLcd.ColorWidth;
    private const int H = LogitechLcd.ColorHeight;

    private static readonly Color Accent = Color.FromArgb(0, 240, 255);
    private static readonly Color TextPrimary = Color.FromArgb(255, 255, 255);
    private static readonly Color TextSecondary = Color.FromArgb(198, 198, 200);
    private static readonly Color TextTertiary = Color.FromArgb(128, 128, 132);
    private static readonly Color Divider = Color.FromArgb(44, 44, 48);

    private const int ArtX = 16, ArtY = 46, ArtSize = 100;
    private const int TextX = 132;
    private const int TextW = W - TextX - 16;
    private const int BarX = 16, BarY = 198, BarW = W - 32, BarH = 5;

    private readonly Bitmap _canvas = new(W, H, PixelFormat.Format32bppArgb);
    private readonly Graphics _g;
    private readonly byte[] _buffer = new byte[LogitechLcd.ColorBufferBytes];

    private readonly Font _fontBrand = new("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _fontTitle = new("Segoe UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _fontArtist = new("Segoe UI", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _fontAlbum = new("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _fontTime = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly StringFormat _sf = new(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };

    private string _artKey = " ";
    private Bitmap? _art;
    private Bitmap? _background;
    private DateTime _trackStartedAt = DateTime.UtcNow;

    public LcdRenderer()
    {
        _g = Graphics.FromImage(_canvas);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        _g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public void Render(NowPlaying np)
    {
        EnsureArtwork(np);

        _g.Clear(Color.Black);
        DrawBackground();
        DrawHeader(np);

        if (!np.HasSession || string.IsNullOrEmpty(np.Title))
        {
            DrawIdle();
        }
        else
        {
            DrawArtwork();
            DrawTrackText(np);
            DrawProgress(np);
        }

        Push();
    }

    // -- artwork -------------------------------------------------------------

    private void EnsureArtwork(NowPlaying np)
    {
        if (np.Key == _artKey) return;

        _artKey = np.Key;
        _trackStartedAt = DateTime.UtcNow;

        _art?.Dispose();
        _background?.Dispose();
        _art = null;
        _background = null;

        if (np.Artwork is null) return;

        // Built into locals and only published once both succeeded. Assigning _art first and
        // then failing in BuildBackground would strand a live GDI bitmap on every such track.
        Bitmap? art = null;
        Bitmap? background = null;

        try
        {
            using var ms = new MemoryStream(np.Artwork);
            using var source = new Bitmap(ms);

            art = ScaleToCover(source, ArtSize, ArtSize);
            background = BuildBackground(source);

            (_art, _background) = (art, background);
            (art, background) = (null, null);
        }
        catch
        {
            // Corrupt or unsupported thumbnail - fall back to the plain black screen.
            _art = null;
            _background = null;
        }
        finally
        {
            art?.Dispose();
            background?.Dispose();
        }
    }

    /// <summary>
    /// Cheap blur: shrink hard, grow back with bilinear filtering. At 320x240 the result is
    /// indistinguishable from a real gaussian and costs a fraction of the time.
    /// </summary>
    private static Bitmap BuildBackground(Image source)
    {
        using var tiny = new Bitmap(40, 30, PixelFormat.Format32bppArgb);
        using (var tg = Graphics.FromImage(tiny))
        {
            tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
            tg.PixelOffsetMode = PixelOffsetMode.HighQuality;
            tg.DrawImage(source, new Rectangle(0, 0, 40, 30));
        }

        var result = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(tiny, new Rectangle(-8, -8, W + 16, H + 16));

        // Dim it far enough that white text stays readable over any album cover.
        using var dim = new SolidBrush(Color.FromArgb(196, 0, 0, 0));
        g.FillRectangle(dim, 0, 0, W, H);

        using var vignette = new LinearGradientBrush(
            new Rectangle(0, H / 2, W, H / 2 + 1),
            Color.FromArgb(0, 0, 0, 0), Color.FromArgb(140, 0, 0, 0), LinearGradientMode.Vertical);
        g.FillRectangle(vignette, 0, H / 2, W, H / 2);

        return result;
    }

    private static Bitmap ScaleToCover(Image source, int width, int height)
    {
        var scale = Math.Max((float)width / source.Width, (float)height / source.Height);
        var w = source.Width * scale;
        var h = source.Height * scale;

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, (width - w) / 2f, (height - h) / 2f, w, h);
        return result;
    }

    // -- screen sections -----------------------------------------------------

    private void DrawBackground()
    {
        if (_background is not null) _g.DrawImage(_background, 0, 0);
    }

    private void DrawHeader(NowPlaying np)
    {
        using var accent = new SolidBrush(Accent);
        using var secondary = new SolidBrush(TextSecondary);
        using var divider = new Pen(Divider);

        _g.FillEllipse(accent, 16, 15, 6, 6);
        _g.DrawString("TIDAL", _fontBrand, secondary, 28, 12, _sf);

        var state = !np.HasSession ? "OFFLINE" : np.IsPlaying ? "PLAYING" : "PAUSED";
        var stateWidth = _g.MeasureString(state, _fontBrand, int.MaxValue, _sf).Width;
        using var stateBrush = new SolidBrush(np.IsPlaying ? Accent : TextTertiary);
        _g.DrawString(state, _fontBrand, stateBrush, W - 16 - stateWidth, 12, _sf);

        _g.DrawLine(divider, 16, 32, W - 16, 32);
    }

    private void DrawIdle()
    {
        using var primary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);

        const string headline = "Nichts wird abgespielt";
        var hw = _g.MeasureString(headline, _fontArtist, int.MaxValue, _sf).Width;
        _g.DrawString(headline, _fontArtist, primary, (W - hw) / 2f, 108, _sf);

        const string hint = "TIDAL starten und Wiedergabe beginnen";
        var sw = _g.MeasureString(hint, _fontAlbum, int.MaxValue, _sf).Width;
        _g.DrawString(hint, _fontAlbum, tertiary, (W - sw) / 2f, 132, _sf);
    }

    private void DrawArtwork()
    {
        var frame = new Rectangle(ArtX, ArtY, ArtSize, ArtSize);

        if (_art is not null)
        {
            _g.DrawImage(_art, frame);
        }
        else
        {
            // No cover art: a flat tile with a disc motif reads better than an empty hole.
            using var placeholder = new SolidBrush(Color.FromArgb(255, 28, 28, 32));
            _g.FillRectangle(placeholder, frame);

            using var ring = new Pen(Color.FromArgb(90, 90, 96), 2f);
            _g.DrawEllipse(ring, ArtX + 26, ArtY + 26, 48, 48);
            using var hub = new SolidBrush(Color.FromArgb(90, 90, 96));
            _g.FillEllipse(hub, ArtX + 44, ArtY + 44, 12, 12);
        }

        using var border = new Pen(Color.FromArgb(70, 255, 255, 255));
        _g.DrawRectangle(border, frame);
    }

    private void DrawTrackText(NowPlaying np)
    {
        using var primary = new SolidBrush(TextPrimary);
        using var secondary = new SolidBrush(TextSecondary);
        using var tertiary = new SolidBrush(TextTertiary);

        DrawScrolling(np.Title, _fontTitle, primary, TextX, 46, TextW);
        DrawScrolling(np.Artist, _fontArtist, secondary, TextX, 76, TextW);
        DrawScrolling(np.Album, _fontAlbum, tertiary, TextX, 98, TextW);
    }

    /// <summary>
    /// Draws text clipped to <paramref name="maxWidth"/>, panning it back and forth when it
    /// does not fit. The phase is derived from the time since the track changed, so all three
    /// lines scroll in sync and no per-line state is needed.
    /// </summary>
    private void DrawScrolling(string text, Font font, Brush brush, int x, int y, int maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return;

        var width = _g.MeasureString(text, font, int.MaxValue, _sf).Width;
        var overflow = width - maxWidth;

        float offset = 0;
        if (overflow > 0.5f)
        {
            const float speed = 24f;   // pixels per second
            const float hold = 1.8f;   // seconds paused at each end
            var travel = overflow / speed;
            var cycle = 2 * (travel + hold);
            var t = (float)((DateTime.UtcNow - _trackStartedAt).TotalSeconds % cycle);

            if (t < hold) offset = 0;
            else if (t < hold + travel) offset = (t - hold) / travel * overflow;
            else if (t < 2 * hold + travel) offset = overflow;
            else offset = overflow - (t - 2 * hold - travel) / travel * overflow;
        }

        var clip = new Rectangle(x, y - 2, maxWidth, (int)font.Size + 10);
        _g.SetClip(clip);
        _g.DrawString(text, font, brush, x - offset, y, _sf);
        _g.ResetClip();
    }

    private void DrawProgress(NowPlaying np)
    {
        var position = np.EffectivePosition;
        var duration = np.Duration;

        var fraction = duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1)
            : 0;

        using var track = new SolidBrush(Color.FromArgb(150, 60, 60, 66));
        _g.FillRectangle(track, BarX, BarY, BarW, BarH);

        var fillWidth = (float)(BarW * fraction);
        if (fillWidth > 0)
        {
            using var fill = new LinearGradientBrush(
                new RectangleF(BarX, BarY, Math.Max(fillWidth, 1), BarH),
                Color.FromArgb(0, 190, 220), Accent, LinearGradientMode.Horizontal);
            _g.FillRectangle(fill, BarX, BarY, fillWidth, BarH);

            using var knob = new SolidBrush(Color.White);
            _g.FillEllipse(knob, BarX + fillWidth - 4, BarY - 2, 9, 9);
        }

        using var timeBrush = new SolidBrush(TextSecondary);
        var elapsed = Format(position);
        var total = duration > TimeSpan.Zero ? Format(duration) : "--:--";
        var totalWidth = _g.MeasureString(total, _fontTime, int.MaxValue, _sf).Width;

        _g.DrawString(elapsed, _fontTime, timeBrush, BarX, BarY + 10, _sf);
        _g.DrawString(total, _fontTime, timeBrush, BarX + BarW - totalWidth, BarY + 10, _sf);
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes}:{value.Seconds:00}";

    // -- output --------------------------------------------------------------

    /// <summary>
    /// Copies the canvas into the SDK buffer. Format32bppArgb is B,G,R,A in memory on
    /// little-endian, which is exactly the BGRA layout LogiLcdColorSetBackground expects,
    /// so this is a straight blit.
    /// </summary>
    private void Push()
    {
        var data = _canvas.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = W * 4;
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, _buffer, 0, _buffer.Length);
            }
            else
            {
                for (var y = 0; y < H; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), _buffer, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _canvas.UnlockBits(data);
        }

        LogitechLcd.LogiLcdColorSetBackground(_buffer);
        LogitechLcd.LogiLcdUpdate();
    }

    public void Dispose()
    {
        _art?.Dispose();
        _background?.Dispose();
        _sf.Dispose();
        _fontBrand.Dispose();
        _fontTitle.Dispose();
        _fontArtist.Dispose();
        _fontAlbum.Dispose();
        _fontTime.Dispose();
        _g.Dispose();
        _canvas.Dispose();
    }
}
