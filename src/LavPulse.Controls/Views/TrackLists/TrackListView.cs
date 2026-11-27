using Microsoft.Maui.Layouts;

namespace Laveyana.LavPulse.Controls;

/// <summary>Scrolls and reuses fixed-height track rows, keeping only the viewport and its neighbors attached.</summary>
internal sealed class TrackListView<TItem, TRow> : Grid where TItem : class where TRow : TrackListRow
{
    private readonly AbsoluteLayout _content = new() { VerticalOptions = LayoutOptions.Start };
    private readonly ScrollView _scroll = new();
    // This viewport-relative layer never scrolls. Its full-width opaque surface
    // masks the gaps, rounded corners and indentation of the actual pinned rows.
    private readonly AbsoluteLayout _pinnedLayer = new()
    {
        VerticalOptions = LayoutOptions.Start,
        HorizontalOptions = LayoutOptions.Fill,
        IsClippedToBounds = true,
        IsVisible = false,
        ZIndex = 100
    };
    private readonly Label _empty = MixerStyle.Label("", MixerStyle.PhoneFont(12, 11), MixerColor.Muted);
    private readonly Func<TRow> _create;
    private readonly Action<TRow, TItem, int> _bind;
    private readonly Action<TRow> _release;
    private readonly Func<TItem, TRow, bool>? _keepAttached;
    private readonly Dictionary<TItem, TRow> _realized = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TItem> _visible = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<TRow> _recycled = new();
    private readonly List<TItem> _leaving = [];
    private IReadOnlyList<TItem> _items = [];
    private int[]? _hierarchyParents;
    private readonly Dictionary<TItem, int> _stickySlots = new(ReferenceEqualityComparer.Instance);
    private readonly int[] _pinnedIndexes = new int[TrackListLayout.MaximumPinnedParents];
    private int _pinnedCount;

    public bool IsPinned(TItem item) => _stickySlots.ContainsKey(item);
    private int _first = -1;
    private int _end = -1;
    private bool _refreshing;
    private bool _refreshAgain;
    private double _endPadding;

    public TrackListView(Func<TRow> create, Action<TRow, TItem, int> bind, Action<TRow> release,
        Func<TItem, TRow, bool>? keepAttached = null)
    {
        _create = create;
        _bind = bind;
        _release = release;
        _keepAttached = keepAttached;
        AbsoluteLayout.SetLayoutFlags(_empty, AbsoluteLayoutFlags.WidthProportional);
        AbsoluteLayout.SetLayoutBounds(_empty, new Rect(0, 0, 1, TrackListLayout.RowHeight));
        IsClippedToBounds = true;
        _scroll.Content = _content;
        Children.Add(_scroll);
        MixerStyle.UseColor(_pinnedLayer, BackgroundColorProperty, MixerColor.Background);
        Children.Add(_pinnedLayer);
        UpdateContentHeight();
        _scroll.Scrolled += (_, args) =>
        {
            Refresh();
            Scrolled?.Invoke(this, args);
        };
        _scroll.SizeChanged += (_, _) => Refresh();
        _content.SizeChanged += (_, _) => ContentSizeChanged?.Invoke();
    }

    public event EventHandler<ScrolledEventArgs>? Scrolled;
    public double ScrollY => _scroll.ScrollY;
    public Task ScrollToAsync(double x, double y, bool animated) => _scroll.ScrollToAsync(x, y, animated);

    public event Action? RowsRefreshed;
    public event Action? ContentSizeChanged;
    public Dictionary<TItem, TRow>.ValueCollection RealizedRows => _realized.Values;
    public double ContentHeight => _content.HeightRequest;
    public double MeasuredContentHeight => _content.Height;
    public double EndPadding => _endPadding;

    public TRow? FindRow(TItem item) => _realized.GetValueOrDefault(item);

    // Layout bounds are current even before the native arrange pass.
    public double GetRowTop(TItem item) => _stickySlots.TryGetValue(item, out int slot)
        ? (double)_pinnedIndexes[slot] * TrackListLayout.Stride
        : FindRow(item) is { } row ? AbsoluteLayout.GetLayoutBounds(row).Y : 0;

    public bool IsInViewport(int index)
    {
        double top = (double)index * TrackListLayout.Stride;
        return top < ScrollY + Height && top + TrackListLayout.RowHeight > ScrollY;
    }

    public void SetEndPadding(double padding)
    {
        if (_endPadding == padding) return;
        _endPadding = padding;
        UpdateContentHeight();
    }

    public void SetItems(IReadOnlyList<TItem> items, string? emptyText = null, int[]? hierarchyParents = null)
    {
        if (hierarchyParents is not null && hierarchyParents.Length != items.Count)
            throw new ArgumentException("Parent indexes must match the flattened list.", nameof(hierarchyParents));
        _hierarchyParents = hierarchyParents;
        _items = items;
        if (_empty.Text != emptyText) _empty.Text = emptyText;
        bool showEmpty = items.Count == 0 && !string.IsNullOrEmpty(emptyText);
        if (showEmpty && !_content.Children.Contains(_empty)) _content.Children.Add(_empty);
        else if (!showEmpty && _content.Children.Contains(_empty)) _content.Children.Remove(_empty);
        UpdateContentHeight();
        Refresh(force: true);
    }

    public void Refresh(bool force = false)
    {
        if (!IsVisible) return;
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            RefreshCore(force);
            RowsRefreshed?.Invoke();
        }
        finally
        {
            _refreshing = false;
            if (_refreshAgain)
            {
                _refreshAgain = false;
                Dispatcher.Dispatch(() => Refresh(force: true));
            }
        }
    }

    private void UpdateContentHeight()
    {
        double height = TrackListLayout.RowsHeight(_items.Count) + _endPadding;
        if (_content.HeightRequest != height) _content.HeightRequest = height;
    }

    private void RefreshCore(bool force)
    {
        double height = _scroll.Height > 0 ? _scroll.Height :
            Height > 0 ? Height : Math.Max(TrackListLayout.RowHeight, HeightRequest);
        double scroll = Math.Clamp(ScrollY, 0, Math.Max(0, ContentHeight - height));
        var (first, end) = TrackListLayout.RealizedRange(_items.Count, scroll, height);
        Span<int> nextPinned = stackalloc int[TrackListLayout.MaximumPinnedParents];
        int nextCount = _hierarchyParents is { } parents
            ? TrackListLayout.PinnedParents(parents, scroll, nextPinned[..TrackListLayout.PinnedCapacity(height)]) : 0;
        bool chainChanged = nextCount != _pinnedCount ||
            !nextPinned[..nextCount].SequenceEqual(_pinnedIndexes.AsSpan(0, _pinnedCount));
        if (!force && !chainChanged && first == _first && end == _end) return;
        nextPinned[..nextCount].CopyTo(_pinnedIndexes);
        _pinnedCount = nextCount;
        int previousFirst = _first;
        int previousEnd = _end;
        _first = first;
        _end = end;

        _visible.Clear();
        for (int i = first; i < end; i++)
            _visible.Add(_items[i]);
        _stickySlots.Clear();
        for (int slot = 0; slot < _pinnedCount; slot++)
        {
            var item = _items[_pinnedIndexes[slot]];
            _stickySlots.Add(item, slot);
            _visible.Add(item);
        }
        foreach (var (item, row) in _realized)
        {
            if (!_visible.Contains(item) && _keepAttached?.Invoke(item, row) != true)
                _leaving.Add(item);
        }
        foreach (var item in _leaving)
        {
            var row = _realized[item];
            _realized.Remove(item);
            if (_pinnedLayer.Children.Remove(row)) row.ResetSwipe();
            else _content.Children.Remove(row);
            _release(row);
            _recycled.Push(row);
        }
        _leaving.Clear();

        for (int i = first; i < end; i++)
            RealizeRow(i, force || i < previousFirst || i >= previousEnd);
        // Reuse the actual row, even outside the normal overscan. There is
        // never a second folder view or a per-scroll translation to maintain.
        for (int slot = 0; slot < _pinnedCount; slot++)
        {
            int index = _pinnedIndexes[slot];
            if (index < first || index >= end) RealizeRow(index, force);
        }
        double pinnedHeight = TrackListLayout.PinnedAreaHeight(_pinnedCount);
        if (_pinnedLayer.HeightRequest != pinnedHeight) _pinnedLayer.HeightRequest = pinnedHeight;
        _pinnedLayer.IsVisible = _pinnedCount > 0;
    }

    private void RealizeRow(int index, bool rebind)
    {
        var item = _items[index];
        bool add = !_realized.TryGetValue(item, out var row);
        if (add)
        {
            row = _recycled.TryPop(out var recycled) ? recycled : _create();
            AbsoluteLayout.SetLayoutFlags(row, AbsoluteLayoutFlags.WidthProportional);
            _realized.Add(item, row);
        }
        bool pinned = _stickySlots.TryGetValue(item, out int slot);
        var target = pinned ? _pinnedLayer : _content;
        bool moved = !add && !ReferenceEquals(row!.Parent, target);
        if (moved)
        {
            // Transfer only when membership changes. Preserve ordinary queue
            // and scan row transforms; the pinned hierarchy does not animate.
            row!.ResetSwipe();
            row.TranslationY = 0;
            if (!_pinnedLayer.Children.Remove(row)) _content.Children.Remove(row);
        }
        var bounds = new Rect(0, (double)(pinned ? slot : index) * TrackListLayout.Stride, 1, TrackListLayout.RowHeight);
        if (AbsoluteLayout.GetLayoutBounds(row!) != bounds) AbsoluteLayout.SetLayoutBounds(row!, bounds);
        if (rebind || add) _bind(row!, item, index);
        if (add || moved) target.Children.Add(row!);
    }
}
