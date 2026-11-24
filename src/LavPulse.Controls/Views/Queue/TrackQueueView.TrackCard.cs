using Laveyana.Audio.Playback;
using Microsoft.Maui.Controls.Shapes;

namespace Laveyana.LavPulse.Controls;

internal sealed partial class TrackQueueView
{
    private sealed class TrackCard : VerticalStackLayout
    {
        private readonly Image _art = new() { Aspect = Aspect.AspectFill };
        private readonly Label _title = MixerStyle.Label("", MixerStyle.PhoneFont(22, 18), bold: true);
        private readonly Label _artist = MixerStyle.Label("", MixerStyle.PhoneFont(13, 11), MixerColor.Muted);
        private readonly Label? _audioInfo;
        private readonly Label _state = MixerStyle.Label("", MixerStyle.PhoneFont(11, 10));
        private readonly Border _cover;
        private readonly Grid _coverRow;
        private readonly VerticalStackLayout _details;
        private CachedTrack? _displayed;
        private PlayerColors _palette = MixerTheme.Default.Artwork;

        public TrackCard(TextAlignment align, bool showAudioInfo = false)
        {
            Spacing = 7;
            bool right = align == TextAlignment.End;
            _cover = new Border
            {
                WidthRequest = 116,
                HeightRequest = 116,
                HorizontalOptions = right ? LayoutOptions.End : LayoutOptions.Start,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                Content = new Grid { Children = { _art, _state } }
            };
            UpdateCoverBackground();
            _title.HorizontalTextAlignment = _artist.HorizontalTextAlignment = align;
            _title.MaxLines = 2;
            _title.LineBreakMode = LineBreakMode.TailTruncation;
            _artist.MaxLines = 1;
            _artist.LineBreakMode = LineBreakMode.TailTruncation;
            _state.HorizontalOptions = LayoutOptions.End;
            _state.VerticalOptions = LayoutOptions.End;
            MixerStyle.UseColor(_state, BackgroundColorProperty, MixerColor.Background);
            _state.Margin = 6;
            _state.Padding = new Thickness(5, 2);
            _coverRow = new Grid
            {
                ColumnSpacing = CoverInfoGap,
                ColumnDefinitions = showAudioInfo ? [new(GridLength.Auto), new(GridLength.Star)] : [new(GridLength.Star)]
            };
            _coverRow.Add(_cover);
            if (showAudioInfo)
            {
                _audioInfo = MixerStyle.Label("", MixerStyle.PhoneFont(11, 9.5), MixerColor.Muted);
                _audioInfo.HorizontalTextAlignment = TextAlignment.Start;
                _audioInfo.VerticalOptions = LayoutOptions.Center;
                _audioInfo.IsVisible = false;
                _coverRow.Add(_audioInfo, column: 1);
            }
            _details = new VerticalStackLayout
            {
                Spacing = MixerStyle.IsPhone ? 4 : 7,
                Children = { _title, _artist }
            };
            Children.Add(_coverRow);
            Children.Add(_details);
        }

        public void ApplyPalette(PlayerColors colors)
        {
            if (_palette == colors) return;
            _palette = colors;
            UpdateCoverBackground();
        }

        private void UpdateCoverBackground() => _cover.Background =
            _displayed is { Artwork.IsEmpty: false } ? null : TrackCover.CreateGradient(_palette, _displayed?.Name);

        /// <summary>Shows imported tags and audio properties, with a gradient as the artwork fallback.</summary>
        public void Show(CachedTrack? track, string state = "")
        {
            if (!ReferenceEquals(_displayed, track))
            {
                _displayed = track;
                UpdateCoverBackground();
                _art.Source = TrackCover.FromArtwork(track?.Artwork ?? default);
                if (_audioInfo is { } details)
                {
                    details.Text = track?.AudioInfo is { } audio ? FormatAudioInfo(audio) : "";
                    details.IsVisible = track?.AudioInfo is not null;
                }
            }
            _title.Text = track?.Title ?? "Трек не выбран";
            _artist.Text = track is null ? "" : track.Artist ?? "Неизвестный исполнитель";
            _artist.IsVisible = track is not null;
            _state.Text = state;
            _state.IsVisible = state.Length > 0;
        }

        /// <summary>Formats the source properties as four compact lines next to the cover.</summary>
        private static string FormatAudioInfo(TrackAudioInfo audio)
        {
            string mode = audio.ChannelMode switch
            {
                TrackChannelMode.JointStereo => "Joint Stereo",
                TrackChannelMode.DualChannel => "Dual Channel",
                TrackChannelMode.Mixed => "Смешанный режим",
                _ => audio.ChannelMode.ToString()
            };
            string bitrate = audio.IsVariableBitrate ? $"{audio.BitrateKbps} kbps · VBR" : $"{audio.BitrateKbps} kbps";
            return $"{audio.Codec}\n{audio.SampleRate / 1000d:0.###} kHz\n{bitrate}\n{mode}";
        }

        /// <summary>Fits the artwork and leaves the full column width for the title and artist.</summary>
        public void ResizeCover(double side, double columnWidth)
        {
            _cover.WidthRequest = _cover.HeightRequest = side;
            _coverRow.WidthRequest = _details.WidthRequest = columnWidth;
        }

        public double CoverWidth => _cover.Width > 0 ? _cover.Width : _cover.WidthRequest;
    }
}
