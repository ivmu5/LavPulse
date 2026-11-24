using Laveyana.Audio.Playback;
using System.Diagnostics;
using Font = Microsoft.Maui.Graphics.Font;

namespace Laveyana.LavPulse.Controls;

/// <summary>Provides the tags and preparation state displayed in one queue row.</summary>
public readonly record struct QueueTrackItem(string Title, string? Artist, ReadOnlyMemory<byte> Artwork, bool IsAnalyzing)
{
    /// <summary>Whether the track is ready for playback.</summary>
    public bool IsLoaded { get; init; } = true;
}

internal sealed class QueueTrackRow : TrackListRow
{
    private readonly QueueText _text = new();
    private readonly GraphicsView _graphics;
    private readonly GraphicsView _spectrum = new PlaybackGraphicsView { InputTransparent = true, IsVisible = false };
    private QueueTrackItem? _shown;
    private bool _played;
    private bool _current;
    private long _marqueeStart;
    private bool _loading;
    private MixerTheme? _theme;

    public QueueTrackRow()
    {
        _graphics = new GraphicsView { Drawable = _text, InputTransparent = true };
        _text.OverflowChanged += () => AnimationStateChanged?.Invoke();
        DragHandle = new Image
        {
            WidthRequest = 24,
            HeightRequest = 36,
            VerticalOptions = LayoutOptions.Center,
            Aspect = Aspect.AspectFit,
            IsVisible = false
        };
        MixerStyle.UseIcon(DragHandle, Image.SourceProperty, "player_reorder.png");
        SemanticProperties.SetDescription(DragHandle, "Перетащить трек");
        SetDetails(_graphics);
        SetActions(DragHandle);
        Foreground.Children.Insert(0, _spectrum);
        EnableDelete();
        DescribeDelete("Убрать трек из очереди");
    }

    public View DragHandle { get; }
    public bool IsPlayed => _played;
    public bool NeedsAnimation => File is not null && (_loading || _text.IsOverflowing);
    public TrackFile? File { get; private set; }
    public int Index { get; private set; }
    public event Action? AnimationStateChanged;

    public void SetEditorActions(bool reorder, bool delete)
    {
        if (DragHandle.IsVisible != reorder)
        {
            DragHandle.IsVisible = reorder;
            SetActionWidth(new GridLength(reorder ? 30 : 0));
        }
        SetInlineDelete(delete);
    }

    public void Bind(TrackFile file, int index)
    {
        File = file;
        Index = index;
    }

    public void Unbind()
    {
        File = null;
        _shown = null;
        SetArtwork(null);
        Scale = 1;
        SetDeleteReveal(0);
        Opacity = 1;
    }
    public void SetSpectrum(IDrawable? drawable) => _spectrum.Drawable = drawable;
    public void InvalidateSpectrum() => _spectrum.Invalidate();

    /// <summary>Updates only changed tags or colors so pending rows can stay under the pointer.</summary>
    public void Show(QueueTrackItem item, bool current, bool played, MixerTheme theme)
    {
        played &= !current;
        if (_shown == item && _current == current && _played == played && ReferenceEquals(_theme, theme))
        {
            return;
        }
        bool animated = NeedsAnimation;
        SetArtwork(File?.Name, item.Artwork);
        if (_shown?.Title != item.Title || _shown?.Artist != item.Artist)
        {
            _text.ResetWidths();
            _text.Title = SingleLine(item.Title);
            _text.Artist = item.Artist is { } artist ? SingleLine(artist) : null;
            _marqueeStart = Stopwatch.GetTimestamp();
            _text.Seconds = 0;
        }
        _shown = item;
        _current = current;
        _spectrum.IsVisible = current;
        if (current) _spectrum.Invalidate();
        _played = played;
        _loading = !current && !played && item.IsAnalyzing;
        _theme = theme;
        var accent = theme[MixerColor.Accent];
        var muted = theme[MixerColor.Muted];
        _text.Color = played ? Gray(muted) : theme[MixerColor.Text];
        _text.ArtistColor = played ? Gray(muted) : muted;
        _text.Status = current ? "Воспроизводится" : null;
        _text.StatusColor = accent;
        Stroke = current ? accent : Colors.Transparent;
        BackgroundColor = current ? theme[MixerColor.Selected] :
            played ? Gray(theme[MixerColor.Surface]) : theme[MixerColor.Button];
        CoverOpacity = played ? .55 : 1;
        if (!_loading) Opacity = current || played || item.IsLoaded ? 1 : .56;

        string status = current ? "Воспроизводится" :
            played ? "Прослушан" :
            _loading ? "Подготовка" :
            item.IsLoaded ? "Готов" : "Не подготовлен";
        SemanticProperties.SetDescription(this, string.Join(". ", new[] { item.Title, item.Artist, status }
            .Where(text => !string.IsNullOrWhiteSpace(text))));
        _graphics.Invalidate();
        if (animated != NeedsAnimation) AnimationStateChanged?.Invoke();
    }

    public void Animate(double seconds)
    {
        if (_loading) DrawLoadingPulse(seconds);
        if (_text.IsOverflowing)
        {
            _text.Seconds = Stopwatch.GetElapsedTime(_marqueeStart).TotalSeconds;
            _graphics.Invalidate();
        }
    }

    private static Color Gray(Color color)
    {
        float gray = .2126f * color.Red + .7152f * color.Green + .0722f * color.Blue;
        return new Color(gray, gray, gray);
    }

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ');

    private sealed class QueueText : IDrawable
    {
        private static readonly Font TitleFont = new("Inter", 600);
        private static readonly Font ArtistFont = new("Inter");
        public string Title { get; set; } = "";
        public string? Artist { get; set; }
        public Color Color { get; set; } = Colors.White;
        public Color ArtistColor { get; set; } = Colors.White;
        public string? Status { get; set; }
        public Color StatusColor { get; set; } = Colors.White;
        public double Seconds { get; set; }
        public bool IsOverflowing { get; private set; }
        public event Action? OverflowChanged;
        private readonly record struct Fragment(string Text, float Width);
        private readonly Dictionary<(string Text, Font Font, float Size), Fragment[]> _fragments = new();

        public void ResetWidths()
        {
            _fragments.Clear();
            IsOverflowing = false;
        }

        public void Draw(ICanvas canvas, RectF bounds)
        {
            bool overflowed = IsOverflowing;
            IsOverflowing = false;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                if (overflowed) OverflowChanged?.Invoke();
                return;
            }
            canvas.SaveState();
            canvas.ClipRectangle(bounds);
            if (Status is { } status)
            {
                float lineHeight = bounds.Height / (string.IsNullOrWhiteSpace(Artist) ? 2 : 3);
                DrawLine(canvas, new(bounds.X, bounds.Y, bounds.Width, lineHeight), status, ArtistFont, (float)MixerStyle.PhoneFont(10, 9), StatusColor);
                DrawLine(canvas, new(bounds.X, bounds.Y + lineHeight, bounds.Width, lineHeight), Title, TitleFont, (float)MixerStyle.PhoneFont(12, 11), Color);
                if (!string.IsNullOrWhiteSpace(Artist))
                {
                    DrawLine(canvas, new(bounds.X, bounds.Y + 2 * lineHeight, bounds.Width, lineHeight), Artist, ArtistFont, (float)MixerStyle.PhoneFont(10, 9), ArtistColor);
                }
            }
            else if (string.IsNullOrWhiteSpace(Artist))
            {
                DrawLine(canvas, bounds, Title, TitleFont, (float)MixerStyle.PhoneFont(13, 12), Color);
            }
            else
            {
                float center = bounds.Center.Y;
                DrawLine(canvas, new(bounds.X, center - 17, bounds.Width, 18), Title, TitleFont, (float)MixerStyle.PhoneFont(13, 12), Color);
                DrawLine(canvas, new(bounds.X, center + 1, bounds.Width, 17), Artist, ArtistFont, (float)MixerStyle.PhoneFont(11, 10), ArtistColor);
            }
            canvas.RestoreState();
            if (overflowed != IsOverflowing) OverflowChanged?.Invoke();
        }

        private void DrawLine(ICanvas canvas, RectF bounds, string value, Font font, float size, Color color)
        {
            var key = (value, font, size);
            if (!_fragments.TryGetValue(key, out var pieces))
            {
                // Never hand a whole long title to a platform renderer. Some native
                // text backends wrap it even with the baseline DrawString overload.
                // Short, grapheme-safe spans prevent a second line from appearing.
                pieces = QueueTextFragments.Split(value)
                    .Select(part => new Fragment(part,
                        Math.Max(0, canvas.GetStringSize(part, font, size).Width)))
                    .ToArray();
                _fragments[key] = pieces;
            }
            float total = 0;
            foreach (var piece in pieces) total += piece.Width;
            float overflow = Math.Max(0, total - bounds.Width);
            IsOverflowing |= overflow > 0;
            float offset = QueueMarqueeMotion.Offset(overflow, Seconds);
            float baseline = QueueMarqueeMotion.Baseline(bounds.Center.Y, size);
            canvas.Font = font;
            canvas.FontSize = size;
            canvas.FontColor = color;
            canvas.SaveState();
            canvas.ClipRectangle(bounds);
            float x = bounds.Left - offset;
            foreach (var part in pieces)
            {
                if (x + part.Width >= bounds.Left && x <= bounds.Right)
                    canvas.DrawString(part.Text, x, baseline, HorizontalAlignment.Left);
                x += part.Width;
                if (x > bounds.Right) break;
            }
            canvas.RestoreState();
        }
    }
}
