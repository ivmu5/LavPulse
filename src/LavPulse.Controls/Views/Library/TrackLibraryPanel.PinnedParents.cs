namespace Laveyana.LavPulse.Controls;

public sealed partial class TrackLibraryPanel
{
    private int[] _parents = [];
    private readonly VerticalStackLayout _pinnedParents = new() { Spacing = 3, IsVisible = false };
    private readonly Button[] _parentButtons = new Button[TrackListLayout.MaximumPinnedParents];
    private readonly TreeRow?[] _pinnedRows = new TreeRow?[TrackListLayout.MaximumPinnedParents];

    private View CreatePinnedParents()
    {
        for (int i = 0; i < _parentButtons.Length; i++)
        {
            int slot = i;
            var button = MixerStyle.Button("", icon: "player_folder.png");
            button.MinimumHeightRequest = 32;
            button.HeightRequest = 32;
            button.Padding = new Thickness(8, 3);
            button.FontSize = MixerStyle.PhoneFont(12, 11);
            button.LineBreakMode = LineBreakMode.TailTruncation;
            button.HorizontalOptions = LayoutOptions.Fill;
            button.IsVisible = false;
            MixerStyle.UseColor(button, BackgroundColorProperty, MixerColor.Selected);
            button.Clicked += async (_, _) =>
            {
                if (_pinnedRows[slot] is not { Expanded: true } parent || _busy || _closed) return;
                await RunAsync(async () =>
                {
                    int index = _rows.IndexOf(parent);
                    if (index < 0) return;
                    await SelectAsync(parent);
                    // The collapsed parent becomes the scroll anchor, rather than
                    // leaving the user in the next sibling at an obsolete offset.
                    await _tree.ScrollToAsync(0, index * (double)TrackListLayout.Stride, false);
                    _scrollOffset = Math.Max(0, _tree.ScrollY);
                    KeepScrollOffset(_rows.Count);
                    RefreshPinnedParents();
                }, "Не удалось свернуть папку");
            };
            _parentButtons[i] = button;
            _pinnedParents.Add(button);
        }
        return _pinnedParents;
    }

    private void PublishTree()
    {
        _parents = TrackListLayout.ParentIndexes(_rows.Select(row => row.Depth).ToArray());
        _tree.SetItems(_rows);
        RefreshPinnedParents();
    }

    private void RefreshPinnedParents()
    {
        if (_parentButtons[0] is null) return;
        Span<int> indexes = stackalloc int[TrackListLayout.MaximumPinnedParents];
        int count = _closed || _scanBatch is not null || !_tree.IsVisible
            ? 0 : TrackListLayout.PinnedParents(_parents, _scrollOffset, indexes);
        for (int i = 0; i < _parentButtons.Length; i++)
        {
            var button = _parentButtons[i];
            TreeRow? row = i < count ? _rows[indexes[i]] : null;
            if (!ReferenceEquals(_pinnedRows[i], row))
            {
                _pinnedRows[i] = row;
                if (row is not null)
                {
                    string name = row.Folders[^1].Name;
                    button.Text = "▾  " + name;
                    SemanticProperties.SetDescription(button, "Свернуть папку " + name);
                }
            }
            button.IsEnabled = !_busy;
            button.IsVisible = row is not null;
        }
        _pinnedParents.IsVisible = count > 0;
    }
}
