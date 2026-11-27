namespace Laveyana.LavPulse.Controls;

public sealed partial class TrackLibraryPanel
{
    private void PublishTree() =>
        _tree.SetItems(_rows, hierarchyParents: TrackListLayout.ParentIndexes(_rows.Select(row => row.Depth).ToArray()));
}
