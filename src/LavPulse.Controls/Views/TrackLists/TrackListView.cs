using Microsoft.Maui.Layouts;

namespace Laveyana.LavPulse.Controls;

/// <summary>Scrolls and reuses fixed-height track rows, keeping only the viewport and its neighbors attached.</summary>
internal sealed class TrackListView<TItem, TRow> : ScrollView where TItem : class where TRow : TrackListRow
{
    private readonly AbsoluteLayout _content = new() { VerticalOptions = LayoutOptions.Start };
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
        Content = _content;
        UpdateContentHeight();
        Scrolled += (_, _) => Refresh();
        SizeChanged += (_, _) => Refresh();
        _content.SizeChanged += (_, _) => ContentSizeChanged?.Invoke();
    }

    public event Action? RowsRefreshed;
    public event Action? ContentSizeChanged;
    public Dictionary<TItem, TRow>.ValueCollection RealizedRows => _realized.Values;
    public double ContentHeight => _content.HeightRequest;
    public double MeasuredContentHeight => _content.Height;
    public double EndPadding => _endPadding;

    public TRow? FindRow(TItem item) => _realized.GetValueOrDefault(item);

    // Layout bounds are current even before the native arrange pass.
    public double GetRowTop(TItem item) => FindRow(item) is { } row
        ? AbsoluteLayout.GetLayoutBounds(row).Y : 0;

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
        if (_hierarchyParents is not null && hierarchyParents is null)
        {
            foreach (var item in _stickySlots.Keys)
                if (_realized.TryGetValue(item, out var row)) ResetSticky(row);
            _stickySlots.Clear();
            _pinnedCount = 0;
        }
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
        double height = Height > 0 ? Height : Math.Max(TrackListLayout.RowHeight, HeightRequest);
        double scroll = Math.Clamp(ScrollY, 0, Math.Max(0, ContentHeight - height));
        var (first, end) = TrackListLayout.RealizedRange(_items.Count, scroll, height);
        if (_hierarchyParents is null && !force && first == _first && end == _end) return;
        int previousFirst = _first;
        int previousEnd = _end;
        _first = first;
        _end = end;

        _visible.Clear();
        for (int i = first; i < end; i++)
            _visible.Add(_items[i]);
        if (_hierarchyParents is { } parents)
        {
            _stickySlots.Clear();
            int capacity = Math.Clamp((int)((height - TrackListLayout.RowHeight) / TrackListLayout.Stride),
                0, _pinnedIndexes.Length);
            _pinnedCount = TrackListLayout.PinnedParents(parents, scroll, _pinnedIndexes.AsSpan(0, capacity));
            for (int slot = 0; slot < _pinnedCount; slot++)
            {
                var item = _items[_pinnedIndexes[slot]];
                _stickySlots.Add(item, slot);
                _visible.Add(item);
            }
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
            _content.Children.Remove(row);
            if (_hierarchyParents is not null) ResetSticky(row);
            _release(row);
            _recycled.Push(row);
        }
        _leaving.Clear();

        for (int i = first; i < end; i++)
            RealizeRow(i, force || i < previousFirst || i >= previousEnd, scroll);
        // Keep the actual row in the same layout, even outside the normal overscan.
        for (int slot = 0; slot < _pinnedCount; slot++)
        {
            int index = _pinnedIndexes[slot];
            if (index < first || index >= end) RealizeRow(index, force, scroll);
        }
    }

    private void RealizeRow(int index, bool rebind, double scroll)
    {
        var item = _items[index];
        bool add = !_realized.TryGetValue(item, out var row);
        if (add)
        {
            row = _recycled.TryPop(out var recycled) ? recycled : _create();
            AbsoluteLayout.SetLayoutFlags(row, AbsoluteLayoutFlags.WidthProportional);
            _realized.Add(item, row);
        }
        double naturalTop = (double)index * TrackListLayout.Stride;
        var bounds = new Rect(0, naturalTop, 1, TrackListLayout.RowHeight);
        if (AbsoluteLayout.GetLayoutBounds(row!) != bounds) AbsoluteLayout.SetLayoutBounds(row!, bounds);
        if (rebind || add) _bind(row!, item, index);
        if (add) _content.Children.Add(row!);
        if (_hierarchyParents is not null)
        {
            if (_stickySlots.TryGetValue(item, out int slot))
            {
                row!.ZIndex = 100 + slot;
                row.TranslationY = scroll + slot * TrackListLayout.Stride - naturalTop;
            }
            else ResetSticky(row!);
        }
    }

    private static void ResetSticky(TRow row)
    {
        row.TranslationY = 0;
        row.ZIndex = 0;
    }
}
