using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;
using System.Diagnostics;
using Font = Microsoft.Maui.Graphics.Font;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Provides the tags and preparation state displayed in one queue row.</summary>
public readonly record struct QueueTrackItem(string Title, string? Artist, ReadOnlyMemory<byte> Artwork, bool IsAnalyzing)
{
    /// <summary>Whether the track is ready for playback.</summary>
    public bool IsLoaded { get; init; } = true;
}

internal sealed class QueueTrackRow : Border
{
    public const int RowHeight = 54;
    private const int DeleteSize = 44;
    private const int DeleteRevealWidth = DeleteSize + 5;

    private readonly Image _art = new() { Aspect = Aspect.AspectFill };
    private readonly Border _cover;
    private readonly QueueText _text = new();
    private readonly GraphicsView _graphics;
    private readonly Grid _foreground;
    private readonly ImageButton _delete;
    private readonly GraphicsView _spectrum = new() { InputTransparent = true, IsVisible = false };
    private QueueTrackItem? _shown;
    private bool _played;
    private bool _current;
    private long _marqueeStart;
    private bool _loading;
    private MixerTheme? _theme;

    public QueueTrackRow()
    {
        HeightRequest = RowHeight;
        Padding = new Thickness(7, 5);
        StrokeThickness = 1;
        StrokeShape = new RoundRectangle { CornerRadius = 8 };
        _graphics = new GraphicsView { Drawable = _text, InputTransparent = true };
        _text.OverflowChanged += () => AnimationStateChanged?.Invoke();
        DragHandle = new Image { WidthRequest = 24, HeightRequest = 36,
            VerticalOptions = LayoutOptions.Center, Aspect = Aspect.AspectFit };
        MixerStyle.UseIcon(DragHandle, Image.SourceProperty, "player_reorder.png");
        SemanticProperties.SetDescription(DragHandle, "Перетащить трек");
        _cover = new Border
        {
            WidthRequest = 38, HeightRequest = 38, VerticalOptions = LayoutOptions.Center,
            StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 5 }, Content = _art
        };
        var body = new Grid
        {
            BackgroundColor = Colors.Transparent,
            ColumnSpacing = 6, ColumnDefinitions = [new(38), new(GridLength.Star)]
        };
        body.Add(_cover);
        body.Add(_graphics, 1);
        TrackBody = body;
        var content = new Grid { ColumnSpacing = 6, ColumnDefinitions = [new(GridLength.Star), new(30)] };
        content.Add(body);
        content.Add(DragHandle, 1);
        _foreground = new Grid { Children = { _spectrum, content } };
        _delete = MixerStyle.IconButton("player_delete.png", DeleteSize);
        _delete.HorizontalOptions = LayoutOptions.End;
        MixerStyle.UseColor(_delete, BackgroundColorProperty, MixerColor.Selected);
        SemanticProperties.SetDescription(_delete, "Убрать трек из очереди");
        _delete.Clicked += (_, _) => DeleteRequested?.Invoke();
        _delete.Unfocused += (_, _) => DeleteUnfocused?.Invoke();
        Content = new Grid { IsClippedToBounds = true, Children = { _delete, _foreground } };
        SetDeleteReveal(0);
    }

    public View TrackBody { get; }
    public View DragHandle { get; }
    public bool IsPlayed => _played;
    public bool NeedsAnimation => File is not null && (_loading || _text.IsOverflowing);
    public TrackFile? File { get; private set; }
    public int Index { get; private set; }
    public event Action? AnimationStateChanged;
    public event Action? DeleteRequested;
    public event Action? DeleteUnfocused;

    public void SetDeleteReveal(double progress)
    {
        double offset = DeleteRevealWidth * progress;
        _foreground.TranslationX = -offset;
        _delete.TranslationX = DeleteRevealWidth - offset;
        _delete.IsVisible = progress > 0;
        _delete.InputTransparent = progress < 1;
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
        _art.Source = null;
        _cover.Background = null;
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
        bool artworkChanged = _shown?.Artwork.Equals(item.Artwork) != true;
        if (artworkChanged)
        {
            _art.Source = TrackCover.FromArtwork(item.Artwork);
        }
        if (artworkChanged || (item.Artwork.IsEmpty && _theme?.Artwork != theme.Artwork))
        {
            _cover.Background = item.Artwork.IsEmpty ? TrackCover.CreateGradient(theme.Artwork, File?.Name) : null;
        }
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
        _text.Status = current ? "Играет" : null;
        _text.StatusColor = accent;
        Stroke = current ? accent : Colors.Transparent;
        BackgroundColor = current ? theme[MixerColor.Selected] :
            played ? Gray(theme[MixerColor.Surface]) : theme[MixerColor.Button];
        _cover.Opacity = played ? .55 : 1;
        if (!_loading) Opacity = current || played || item.IsLoaded ? 1 : .56;

        string status = current ? "Играет" :
            played ? "Прослушан. Нажми, чтобы начать отсюда." :
            _loading ? "Загружается" :
            item.IsLoaded ? "Загружен" : "Не загружен";
        SemanticProperties.SetDescription(this, string.Join(". ", new[] { item.Title, item.Artist, status }
            .Where(text => !string.IsNullOrWhiteSpace(text))));
        _graphics.Invalidate();
        if (animated != NeedsAnimation) AnimationStateChanged?.Invoke();
    }

    public void Animate(double seconds)
    {
        if (_loading)
        {
            Opacity = .76 + .24 * Math.Cos(seconds * 4.2);
        }
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
        private readonly Dictionary<(string Text, Font Font, float Size), float> _widths = new();

        public void ResetWidths()
        {
            _widths.Clear();
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
            if (!_widths.TryGetValue(key, out float width))
            {
                width = canvas.GetStringSize(value, font, size).Width + 1;
                _widths[key] = width;
            }
            float overflow = Math.Max(0, width - bounds.Width);
            IsOverflowing |= overflow > 0;
            // Pause at each end and pan slowly enough to read the whole line.
            double travel = overflow / 18d;
            double phase = Seconds % (2 * travel + 2.4);
            float offset = (float)Math.Max(0, Math.Min(overflow, Math.Min(phase - 1.2, 2 * travel + 2.4 - phase) * 18));
            canvas.Font = font;
            canvas.FontSize = size;
            canvas.FontColor = color;
            canvas.DrawString(value, bounds.X - offset, bounds.Y, Math.Max(width, bounds.Width), bounds.Height,
                HorizontalAlignment.Left, VerticalAlignment.Center, TextFlow.ClipBounds);
        }
    }
}
