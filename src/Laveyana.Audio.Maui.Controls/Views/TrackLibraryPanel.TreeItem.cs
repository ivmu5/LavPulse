using System.Diagnostics;
using System.IO;
using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.Audio.Maui.Controls;

public sealed partial class TrackLibraryPanel
{
    private sealed class LibraryTreeItem : TrackListRow
    {
        private readonly TrackLibraryPanel _panel;
        private readonly bool _previewOnly;
        private readonly Label _chevron = MixerStyle.Label("", 14, MixerColor.Accent);
        private readonly Label _title = TrackLabel(MixerStyle.PhoneFont(13, 12), MixerColor.Text, bold: true);
        private readonly Label _details = TrackLabel(MixerStyle.PhoneFont(11, 10), MixerColor.Muted);
        private readonly HorizontalStackLayout _folder;
        private readonly HorizontalStackLayout _actions;
        private readonly ImageButton _add = MixerStyle.IconButton("player_queue_add.png", 36);
        private readonly ImageButton _import = MixerStyle.IconButton("player_import.png", 36);
        private TreeRow? _row;
        private CancellationTokenSource? _preview;

        public LibraryTreeItem(TrackLibraryPanel panel, bool previewOnly = false)
        {
            _panel = panel;
            _previewOnly = previewOnly;
            InputTransparent = previewOnly;

            var icon = new Image { WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center };
            MixerStyle.UseIcon(icon, Image.SourceProperty, "player_folder.png");
            _chevron.WidthRequest = 14;
            _chevron.VerticalOptions = LayoutOptions.Center;
            _folder = new HorizontalStackLayout { Spacing = 4, Children = { _chevron, icon } };
            AddLeading(_folder);
            SetDetails(new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center, Spacing = 2, Children = { _title, _details }
            });
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
            SetActions(_actions);
            if (previewOnly) return;
            EnableDelete();
            DeleteRequested += async () =>
            {
                if (_row is { } row) await panel.RemoveItemAsync(this, row);
            };
            DeleteUnfocused += () =>
            {
                if (ReferenceEquals(panel._revealed, this)) panel.DismissDeleteAction();
            };
            TrackListSwipe.Attach(this, () => _row,
                () => !panel._editing && !panel._busy && panel.CanRemove(_row), () => panel.RevealDelete(this));
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (panel.DismissDeleteAction()) return;
                if (_row is { } row) await _panel.RunAsync(() => _panel.SelectAsync(row), "Не удалось открыть папку");
            };
            Body.GestureRecognizers.Add(tap);
        }

        public void Bind(TreeRow? row)
        {
            if (row == _row) return;
            CancelPreview();
            if (_row is { } old)
            {
                old.Changed -= UpdateRow;
                old.MetadataChanged -= LoadMetadata;
            }
            _row = row;
            SetDeleteReveal(0);
            SetArtwork(row?.File?.Name);
            if (row is null) return;
            row.Changed += UpdateRow;
            row.MetadataChanged += LoadMetadata;
            Margin = new Thickness(Math.Min(9, row.Depth) * 12, 0, 0, 0);
            bool folder = row.File is null;
            _folder.IsVisible = folder;
            ShowCover(!folder);
            _details.Text = "";
            _details.IsVisible = false;
            _title.Text = folder ? row.Folders[^1].Name : Path.GetFileNameWithoutExtension(row.File!.Name);
            UpdateRow();
            LoadMetadata();
        }

        public void UpdateRow()
        {
            if (_row is not { } row) return;
            _chevron.Text = row.Expanded ? "▾" : "▸";
            bool showActions = !_previewOnly && (row.Selected || _panel._editing);
            _actions.IsVisible = showActions;
            SetActionWidth(showActions ? GridLength.Auto : new GridLength(0));
            SetInlineDelete(!_previewOnly && _panel._editing && _panel.CanRemove(row));
            SetDeleteEnabled(!_panel._busy);
            _import.IsVisible = !_panel._local;
            _add.IsEnabled = _import.IsEnabled = !_panel._busy;
            bool folder = row.File is null;
            string add = folder ? "Добавить папку в очередь" : "Добавить трек в очередь";
            DescribeDelete(folder ? "Убрать папку и её треки из медиатеки" : "Убрать трек из медиатеки");
            string import = folder ? "Импортировать папку в медиатеку" : "Импортировать трек в медиатеку";
            SemanticProperties.SetDescription(_add, add);
            SemanticProperties.SetDescription(_import, import);
            ToolTipProperties.SetText(_add, add);
            ToolTipProperties.SetText(_import, import);
            MixerStyle.UseColor(this, BackgroundColorProperty, row.Selected ? MixerColor.Selected : MixerColor.Button);
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
                SetArtwork(file.Name, tags.Artwork);
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
