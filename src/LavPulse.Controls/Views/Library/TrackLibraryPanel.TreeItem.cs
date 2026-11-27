using System.Diagnostics;
using System.IO;
using System.Threading;
using Laveyana.Audio.Playback;

namespace Laveyana.LavPulse.Controls;

public sealed partial class TrackLibraryPanel
{
    private sealed class LibraryTreeItem : TrackListRow
    {
        private readonly TrackLibraryPanel _panel;
        private readonly bool _scanRow;
        private Button? _skipScan;
        private Label? _scanStats;
        private bool _scanCompleted;
        internal TrackScanHistory.Entry? ScanEntry { get; private set; }
        internal bool IsActiveScan => ScanEntry is { Result: null };
        private readonly Label _chevron = MixerStyle.Label("", 14, MixerColor.Accent);
        private readonly Label _title = TrackLabel(MixerStyle.PhoneFont(13, 12), MixerColor.Text, bold: true);
        private readonly Label _details = TrackLabel(MixerStyle.PhoneFont(11, 10), MixerColor.Muted);
        private readonly Label _analysisState = TrackLabel(9,MixerColor.Muted);
        private readonly Border _analysisBadge = new()
        {
            IsVisible = false,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
            Padding = new Thickness(4, 1),
            HorizontalOptions = LayoutOptions.End
        };
        private readonly Label _size = TrackLabel(MixerStyle.PhoneFont(11, 10), MixerColor.Muted);
        private readonly HorizontalStackLayout _folder;
        private readonly ImageButton _add = MixerStyle.IconButton("player_queue_add.png", 36);
        private readonly ImageButton _import = MixerStyle.IconButton("player_import.png", 36);
        private TreeRow? _row;
        private CancellationTokenSource? _preview;

        public LibraryTreeItem(TrackLibraryPanel panel, Action<TrackFile>? skip = null)
        {
            _panel = panel;
            _scanRow = skip is not null;
            _analysisState.Text="Не проанализирован";
            SelectionToggled += () =>
            {
                if(panel._editing&&!panel._busy&&_row is { } selected)panel.ToggleChecked(selected);
            };

            var icon = new Image { WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center };
            MixerStyle.UseIcon(icon, Image.SourceProperty, "player_folder.png");
            _chevron.WidthRequest = 14;
            _chevron.VerticalOptions = LayoutOptions.Center;
            _folder = new HorizontalStackLayout { Spacing = 4, Children = { _chevron, icon } };
            AddLeading(_folder);
            SetDetails(new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center,
                Spacing = 2,
                Children = { _title, _details }
            });
            SetActionWidth(GridLength.Auto);
            _size.VerticalOptions = LayoutOptions.Center;
            _size.HorizontalTextAlignment = TextAlignment.End;
            if (skip is not null)
            {
                var skipButton = _skipScan = MixerStyle.Button("Пропустить");
                _scanStats = MixerStyle.Label("", MixerStyle.PhoneFont(10, 9), MixerColor.Muted);
                _scanStats.IsVisible = false;
                _scanStats.VerticalOptions = LayoutOptions.Center;
                _scanStats.HorizontalTextAlignment = TextAlignment.End;
                _scanStats.LineBreakMode = LineBreakMode.NoWrap;
                _scanStats.MaxLines = 3;
                skipButton.VerticalOptions = LayoutOptions.Center;
                skipButton.Clicked += (_, _) =>
                {
                    if (_row?.File is not { } file) return;
                    skipButton.IsEnabled = false;
                    skip(file);
                };
                SetActions(new HorizontalStackLayout
                {
                    Spacing = 4,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { _size, _scanStats, skipButton }
                });
                return;
            }
            _add.Clicked += async (_, _) =>
            {
                if (_row is { } row) await _panel.ImportItemAsync(row, addToQueue: true);
            };
            _import.Clicked += async (_, _) =>
            {
                if (_row is { } row) await _panel.ImportItemAsync(row, addToQueue: false);
            };
            _analysisState.HorizontalTextAlignment = TextAlignment.End;
            _analysisState.FontSize = MixerStyle.PhoneFont(9, 8);
            _analysisState.LineBreakMode = LineBreakMode.NoWrap;
            _analysisBadge.Content = _analysisState;
            MixerStyle.UseColor(_analysisBadge, BackgroundColorProperty, MixerColor.Selected);
            var status = new VerticalStackLayout
            {
                Spacing = 1, VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.End,
                Children = { _size, _analysisBadge }
            };
            SetActions(new HorizontalStackLayout
            {
                Spacing = 4,
                VerticalOptions = LayoutOptions.Center,
                Children = { status, _add, _import }
            });
            EnableDelete();
            DeleteRequested += async () =>
            {
                if (_row is { } row) await panel.RemoveItemAsync(this, row);
            };
            DeleteUnfocused += () =>
            {
                if (ReferenceEquals(panel._revealed, this)) panel.DismissDeleteAction();
            };
            AttachActions(() => _row, () => !panel._editing && !panel._busy && _row is not null,
                right => right ? panel._listSettings.LibrarySwipeRight :
                    panel._local ? panel._listSettings.LibrarySwipeLeft : ListSwipeAction.None,
                action => _row is { } row ? panel.ExecuteListActionAsync(row,action) : Task.CompletedTask,
                () => _row is { } row ? panel.ExecuteListActionAsync(row,ListSwipeAction.Menu) : Task.CompletedTask,
                () => PlayerColors.For(panel._listSettings),
                () => { foreach(var other in panel._tree.RealizedRows)if(!ReferenceEquals(other,this))other.ResetSwipe(); });
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (ConsumedGesture || panel.DismissDeleteAction()) return;
                if (panel._editing && _row is { } selected) { panel.ToggleChecked(selected); return; }
                if (_row is { } row) await _panel.RunAsync(() => _panel.SelectAsync(row), "Не удалось открыть папку");
            };
            Body.GestureRecognizers.Add(tap);
        }

        internal void BindScan(TrackScanHistory.Entry? entry)
        {
            if (!ReferenceEquals(ScanEntry, entry))
            {
                ScanEntry = entry;
                _scanCompleted = false;
                Opacity = 1;
                if (_skipScan is not null)
                {
                    _skipScan.IsVisible = true;
                    _skipScan.IsEnabled = true;
                }
                if (_scanStats is not null) _scanStats.IsVisible = false;
                Bind(entry is null ? null : new TreeRow([], entry.File));
            }
            if (entry?.Result is { } result && !_scanCompleted) CompleteScan(result);
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
            ResetSwipe();
            SetDeleteReveal(0);
            if (row is null)
            {
                SetArtwork(null);
                return;
            }
            row.Changed += UpdateRow;
            row.MetadataChanged += LoadMetadata;
            Margin = new Thickness(Math.Min(MixerStyle.IsPhone ? 3 : 6, row.Depth) * 8, 0, 0, 0);
            bool folder = row.File is null;
            _folder.IsVisible = folder;
            ShowCover(!folder);
            UpdateSize();
            UpdateRow();
            if (row.File is { } file && _panel._local && _panel._metadataCache.Get(file) is { } cached)
            {
                ApplyMetadata(file, cached);
            }
            else
            {
                SetArtwork(row.File?.Name);
                _details.Text = "";
                _details.IsVisible = false;
                _title.Text = folder ? row.Folders[^1].Name : Path.GetFileNameWithoutExtension(row.File!.Name);
                LoadMetadata();
            }
        }

        public void UpdateRow()
        {
            if (_row is not { } row) return;
            MixerStyle.UseColor(this, BackgroundColorProperty, row.Selected ? MixerColor.Selected : MixerColor.Button);
            if (_scanRow) return;
            _chevron.Text = row.Expanded ? "▾" : "▸";
            SetInlineDelete(false);
            SetSelectionMark(_panel._editing,_panel._checkedRows.Contains(row));
            bool unscanned=_panel._local && row.File is { } file && !_panel._hasAnalysis(file);
            _analysisBadge.IsVisible = unscanned;
            _title.Opacity = _details.Opacity = unscanned ? .56 : 1;
            CoverOpacity = unscanned ? .56 : 1;
            SetDeleteEnabled(!_panel._busy);
            _import.IsVisible = !_panel._local;
            _add.IsEnabled = _import.IsEnabled = !_panel._busy && !_panel._editing;
            _add.IsVisible = !_panel._editing;
            _import.IsVisible = !_panel._local && !_panel._editing;
            bool folder = row.File is null;
            string add = folder ? "Добавить папку в очередь" : "Добавить трек в очередь";
            DescribeDelete(folder ? "Убрать папку и её треки из медиатеки" : "Убрать трек из медиатеки");
            string import = folder ? "Импортировать папку в медиатеку" : "Импортировать трек в медиатеку";
            SemanticProperties.SetDescription(_add, add);
            SemanticProperties.SetDescription(_import, import);
            ToolTipProperties.SetText(_add, add);
            ToolTipProperties.SetText(_import, import);
        }

        public void UpdateSize()
        {
            if (!_scanCompleted && _row is { } row) _size.Text = _panel.SizeText(row);
        }

        internal void CompleteScan(TrackScanResult result)
        {
            if (!_scanRow || _scanStats is null || _skipScan is null) return;
            _scanCompleted = true;
            Opacity = 1;
            _skipScan.IsVisible = false;
            _skipScan.IsEnabled = false;
            _scanStats.IsVisible = true;
            string outcome = result.Outcome switch
            {
                TrackScanOutcome.Scanned => "",
                TrackScanOutcome.Skipped => " · Пропущен",
                TrackScanOutcome.Failed => " · Ошибка",
                _ => " · Остановлен"
            };
            string elapsed = result.Elapsed.TotalSeconds < 60
                ? $"{result.Elapsed.TotalSeconds:0.0} с"
                : $"{(int)result.Elapsed.TotalMinutes}:{result.Elapsed.Seconds:00}";
            _size.Text = FormatSize(result.AfterBytes ?? result.BeforeBytes);
            _scanStats.Text = $"До: {FormatSize(result.BeforeBytes)}\nПосле: {FormatSize(result.AfterBytes)}\n{elapsed}{outcome}";
            SemanticProperties.SetDescription(_scanStats,
                $"Размер до анализа: {FormatSize(result.BeforeBytes)}. После: {FormatSize(result.AfterBytes)}. Время: {elapsed}{outcome}");
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
                if (_panel._local) _panel._metadataCache.Store(file, tags);
                ApplyMetadata(file, tags);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // The filename and gradient remain available even if a provider cannot open its tags.
                Debug.WriteLine(ex);
            }
        }

        private void ApplyMetadata(TrackFile file, TrackMetadata tags)
        {
            _title.Text = tags.Title;
            string? artist = string.IsNullOrWhiteSpace(tags.Artist) ? null : tags.Artist;
            string? album = string.IsNullOrWhiteSpace(tags.Album) ? null : tags.Album;
            _details.Text = artist is null ? album ?? "" : album is null ? artist : $"{artist} · {album}";
            _details.IsVisible = _details.Text.Length > 0;
            SetArtwork(file.Name, tags.Artwork);
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
