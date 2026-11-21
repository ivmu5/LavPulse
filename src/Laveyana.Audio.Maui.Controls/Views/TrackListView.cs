using Microsoft.Maui.Layouts;

namespace Laveyana.Audio.Maui.Controls;

/// <summary>Scrolls and reuses fixed-height track rows, keeping only the viewport and its neighbors attached.</summary>
internal sealed class TrackListView<TItem, TRow> : ScrollView where TItem : class where TRow : TrackListRow
{
    private readonly AbsoluteLayout _content = new();
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

    public void SetItems(IReadOnlyList<TItem> items, string? emptyText = null)
    {
        _items = items;
        _empty.Text = emptyText;
        bool showEmpty = items.Count == 0 && !string.IsNullOrEmpty(emptyText);
        if (showEmpty && !_content.Children.Contains(_empty)) _content.Children.Add(_empty);
        else if (!showEmpty) _content.Children.Remove(_empty);
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

    private void UpdateContentHeight() => _content.HeightRequest = TrackListLayout.RowsHeight(_items.Count) + _endPadding;

    private void RefreshCore(bool force)
    {
        double height = Height > 0 ? Height : Math.Max(TrackListLayout.RowHeight, HeightRequest);
        double scroll = Math.Clamp(ScrollY, 0, Math.Max(0, ContentHeight - height));
        var (first, end) = TrackListLayout.RealizedRange(_items.Count, scroll, height);
        if (!force && first == _first && end == _end) return;
        _first = first;
        _end = end;

        _visible.Clear();
        for (int i = first; i < end; i++)
            _visible.Add(_items[i]);
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
            _release(row);
            _recycled.Push(row);
        }
        _leaving.Clear();

        for (int i = first; i < end; i++)
        {
            var item = _items[i];
            bool add = !_realized.TryGetValue(item, out var row);
            if (add)
            {
                row = _recycled.TryPop(out var recycled) ? recycled : _create();
                AbsoluteLayout.SetLayoutFlags(row, AbsoluteLayoutFlags.WidthProportional);
                _realized.Add(item, row);
            }

            var bounds = new Rect(0, (double)i * TrackListLayout.Stride, 1, TrackListLayout.RowHeight);
            if (AbsoluteLayout.GetLayoutBounds(row!) != bounds) AbsoluteLayout.SetLayoutBounds(row!, bounds);
            _bind(row!, item, i);
            if (add) _content.Children.Add(row!);
        }
    }
}
