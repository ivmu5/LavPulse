using System.Diagnostics;
using System.IO;
using System.Threading;
using Laveyana.Audio.Playback;
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class TrackLibraryPanel
{
    private sealed class LibraryTreeItem : Grid
    {
        private static readonly BindableProperty ArtworkColorsProperty = BindableProperty.Create("ArtworkColors",
            typeof(PlayerColors), typeof(LibraryTreeItem), MixerTheme.Default.Artwork,
            propertyChanged: (bindable, _, _) => ((LibraryTreeItem)bindable).UpdateCoverBackground());

        private readonly TrackLibraryPanel _panel;
        private readonly Border _card;
        private readonly Border _cover;
        private readonly Image _art = new() { Aspect = Aspect.AspectFill };
        private readonly Label _chevron = MixerStyle.Label("", 14, MixerColor.Accent);
        private readonly Label _title = TrackLabel(13, MixerColor.Text, bold: true);
        private readonly Label _details = TrackLabel(11, MixerColor.Muted);
        private readonly Grid _folder;
        private readonly Grid _track;
        private readonly HorizontalStackLayout _actions;
        private readonly ImageButton _add = MixerStyle.IconButton("player_queue_add.png", 36);
        private readonly ImageButton _import = MixerStyle.IconButton("player_import.png", 36);
        private TreeRow? _row;
        private CancellationTokenSource? _preview;
        private bool _active;

        public LibraryTreeItem(TrackLibraryPanel panel)
        {
            _panel = panel;
            HeightRequest = RowHeight;

            var icon = new Image { WidthRequest = 22, HeightRequest = 22, VerticalOptions = LayoutOptions.Center };
            MixerStyle.UseIcon(icon, Image.SourceProperty, "player_folder.png");
            _chevron.WidthRequest = 14;
            _chevron.VerticalOptions = LayoutOptions.Center;
            var folderIcons = new HorizontalStackLayout { Spacing = 6, Children = { _chevron, icon } };
            _folder = new Grid { Children = { folderIcons } };

            _cover = new Border
            {
                WidthRequest = 44, HeightRequest = 44, VerticalOptions = LayoutOptions.Center,
                StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 6 }, Content = _art
            };
            _track = new Grid { Children = { _cover } };
            var content = new Grid { ColumnDefinitions = [new(44), new(GridLength.Star)], ColumnSpacing = 10 };
            content.Add(_folder);
            content.Add(_track);
            content.Add(new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center, Spacing = 2, Children = { _title, _details }
            }, column: 1);
            _add.Clicked += async (_, _) =>
            {
                if (_row is { } row) await _panel.ImportSelectedAsync(row, addToQueue: true);
            };
            _import.Clicked += async (_, _) =>
            {
                if (_row is { } row) await _panel.ImportSelectedAsync(row, addToQueue: false);
            };
            _actions = new HorizontalStackLayout
            {
                Spacing = 3, VerticalOptions = LayoutOptions.Center, Children = { _add, _import }
            };
            var rowContent = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 4 };
            rowContent.Add(content);
            rowContent.Add(_actions, 1);
            _card = new Border
            {
                Margin = new Thickness(0, 2), Padding = new Thickness(9, 0), StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 8 }, Content = rowContent
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (_row is { } row) await _panel.RunAsync(() => _panel.SelectAsync(row), "Не удалось открыть папку");
            };
            content.GestureRecognizers.Add(tap);
            Add(_card);
            SetDynamicResource(ArtworkColorsProperty, MixerStyle.ArtworkColorsResource);

            BindingContextChanged += (_, _) => Bind();
            Loaded += (_, _) => { _active = true; Bind(); };
            Unloaded += (_, _) => { _active = false; Bind(); };
            _active = IsLoaded;
        }

        private void Bind()
        {
            var row = _active ? BindingContext as TreeRow : null;
            if (row == _row) return;
            CancelPreview();
            if (_row is { } old)
            {
                old.Changed -= UpdateRow;
                old.MetadataChanged -= LoadMetadata;
            }
            _row = row;
            if (row is null) return;
            row.Changed += UpdateRow;
            row.MetadataChanged += LoadMetadata;
            _card.Margin = new Thickness(Math.Min(9, row.Depth) * 12, 2, 0, 2);
            bool folder = row.File is null;
            _folder.IsVisible = folder;
            _track.IsVisible = !folder;
            _art.Source = null;
            _details.Text = "";
            _details.IsVisible = false;
            _title.Text = folder ? row.Folders[^1].Name : Path.GetFileNameWithoutExtension(row.File!.Name);
            UpdateRow();
            UpdateCoverBackground();
            LoadMetadata();
        }

        private void UpdateRow()
        {
            if (_row is not { } row) return;
            _chevron.Text = row.Expanded ? "▾" : "▸";
            _actions.IsVisible = row.Selected;
            _import.IsVisible = !_panel._local;
            _add.IsEnabled = _import.IsEnabled = !_panel._busy;
            bool folder = row.File is null;
            string add = folder ? "Добавить папку в очередь" : "Добавить трек в очередь";
            string import = folder ? "Импортировать папку в медиатеку" : "Импортировать трек в медиатеку";
            SemanticProperties.SetDescription(_add, add);
            SemanticProperties.SetDescription(_import, import);
            ToolTipProperties.SetText(_add, add);
            ToolTipProperties.SetText(_import, import);
            MixerStyle.UseColor(_card, BackgroundColorProperty, row.Selected ? MixerColor.Selected : MixerColor.Surface);
        }

        private void UpdateCoverBackground()
        {
            if (_row?.File is not { } file) return;
            _cover.Background = _art.Source is null
                ? TrackCover.CreateGradient((PlayerColors)GetValue(ArtworkColorsProperty), file.Name) : null;
        }

        private void LoadMetadata()
        {
            if (_row?.File is not { } file || _panel._token.IsCancellationRequested) return;
            CancelPreview();
            _preview = CancellationTokenSource.CreateLinkedTokenSource(_panel._token);
            _ = ShowMetadataAsync(file, _preview.Token);
        }

        private async Task ShowMetadataAsync(TrackFile file, CancellationToken token)
        {
            try
            {
                var tags = await _panel._readMetadata(file, token);
                if (token.IsCancellationRequested || _row?.File != file) return;
                _title.Text = tags.Title;
                var details = new[] { tags.Artist, tags.Album }.Where(value => !string.IsNullOrWhiteSpace(value));
                _details.Text = string.Join(" · ", details);
                _details.IsVisible = _details.Text.Length > 0;
                _art.Source = TrackCover.FromArtwork(tags.Artwork);
                UpdateCoverBackground();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // The filename and gradient remain available even if a provider cannot open its tags.
                Debug.WriteLine(ex);
            }
        }

        private void CancelPreview()
        {
            _preview?.Cancel();
            _preview?.Dispose();
            _preview = null;
        }

        private static Label TrackLabel(double size, MixerColor color, bool bold = false)
        {
            var label = MixerStyle.Label("", size, color, bold);
            label.MaxLines = 1;
            label.LineBreakMode = LineBreakMode.TailTruncation;
            return label;
        }
    }
}
