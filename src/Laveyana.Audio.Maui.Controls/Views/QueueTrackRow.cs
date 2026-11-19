using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Font = Microsoft.Maui.Graphics.Font;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Provides the tags and preparation state displayed in one queue row.</summary>
public readonly record struct QueueTrackItem(string Title, string? Artist, ReadOnlyMemory<byte> Artwork, bool IsAnalyzing);

internal sealed class QueueTrackRow : Border
{
    public const int RowHeight = 54;

    private readonly Image _cover = new() { Aspect = Aspect.AspectFill, WidthRequest = 38, HeightRequest = 38, VerticalOptions = LayoutOptions.Center };
    private readonly QueueText _text = new();
    private readonly GraphicsView _graphics;
    private readonly GraphicsView _spectrum = new() { InputTransparent = true, IsVisible = false };
    private readonly Grid _content;
    private QueueTrackItem? _shown;
    private bool _played;
    private bool _current;
    private long _marqueeStart;
    private bool _selected;
    private MixerTheme? _theme;

    public QueueTrackRow()
    {
        HeightRequest = RowHeight;
        Padding = new Thickness(7, 5);
        StrokeThickness = 1;
        StrokeShape = new RoundRectangle { CornerRadius = 8 };
        _graphics = new GraphicsView { Drawable = _text, InputTransparent = true };
        DragHandle = new Image { WidthRequest = 24, HeightRequest = 36,
            VerticalOptions = LayoutOptions.Center, Aspect = Aspect.AspectFit };
        MixerStyle.UseIcon(DragHandle, Image.SourceProperty, "player_reorder.png");
        SemanticProperties.SetDescription(DragHandle, "Перетащить трек");
        _content = new Grid { ColumnSpacing = 5, ColumnDefinitions = [new(0), new(GridLength.Star), new(24)] };
        _content.Add(_cover);
        _content.Add(_graphics, 1);
        _content.Add(DragHandle, 2);
        _cover.IsVisible = false;
        Content = new Grid { IsClippedToBounds = true, Children = { _spectrum, _content } };
    }

    public View DragHandle { get; }
    public bool IsPlayed => _played;
    public TrackFile? File { get; private set; }
    public int Index { get; private set; }

    public void Bind(TrackFile file, int index)
    {
        File = file;
        Index = index;
    }

    public void Unbind()
    {
        File = null;
        _shown = null;
        _cover.Source = null;
        Scale = 1;
        Opacity = 1;
    }
    public void SetSpectrum(IDrawable? drawable) => _spectrum.Drawable = drawable;
    public void InvalidateSpectrum() => _spectrum.Invalidate();

    /// <summary>Updates only changed tags or colors so pending rows can stay under the pointer.</summary>
    public void Show(QueueTrackItem item, bool current, bool played, bool selected, MixerTheme theme)
    {
        if (_shown == item && _current == current && _played == played && _selected == selected && ReferenceEquals(_theme, theme))
        {
            return;
        }
        if (_shown?.Artwork.Equals(item.Artwork) != true)
        {
            var artwork = item.Artwork;
            var bytes = MemoryMarshal.TryGetArray(artwork, out var segment) ? segment : new ArraySegment<byte>(artwork.ToArray());
            _cover.Source = artwork.IsEmpty ? null : ImageSource.FromStream(
                () => new System.IO.MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false));
            _cover.IsVisible = !artwork.IsEmpty;
            _content.ColumnDefinitions[0].Width = artwork.IsEmpty ? new GridLength(0) : GridLength.Auto;
            _content.ColumnSpacing = artwork.IsEmpty ? 0 : 6;
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
        _selected = selected;
        _theme = theme;
        var accent = theme[MixerColor.Accent];
        _text.Color = played ? theme[MixerColor.Muted] : theme[MixerColor.Text];
        _text.ArtistColor = theme[MixerColor.Muted];
        _text.Status = current ? "Играет" : null;
        _text.StatusColor = accent;
        Stroke = current ? accent : selected ? accent.WithAlpha(.65f) : Colors.Transparent;
        BackgroundColor = theme[current || selected ? MixerColor.Selected : played ? MixerColor.Surface : MixerColor.Button];
        SemanticProperties.SetDescription(this, string.Join(". ", new[]
        {
            item.Title, item.Artist, current ? "Играет" :
                _played ? "До текущего трека. Нажми, чтобы начать отсюда." :
                selected ? "Следующий трек. Повторное нажатие запускает сведение." : null,
            item.IsAnalyzing ? "Анализируется" : null
        }.Where(text => !string.IsNullOrWhiteSpace(text))));
        _graphics.Invalidate();
        if (!item.IsAnalyzing)
        {
            Opacity = 1;
        }
    }

    public void Animate(double seconds)
    {
        if (_shown is { IsAnalyzing: true })
        {
            Opacity = .78 + .22 * Math.Cos(seconds * 4.2);
        }
        if (_text.IsOverflowing)
        {
            _text.Seconds = Stopwatch.GetElapsedTime(_marqueeStart).TotalSeconds;
            _graphics.Invalidate();
        }
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
        private readonly Dictionary<(string Text, Font Font, float Size), float> _widths = new();

        public void ResetWidths() => _widths.Clear();

        public void Draw(ICanvas canvas, RectF bounds)
        {
            IsOverflowing = false;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
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
