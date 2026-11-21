using Laveyana.LavPulse.Controls;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    private readonly Stack<Func<bool>> _forwardNavigation = new();
    private bool _navigatingForward;

    protected override bool OnBackButtonPressed() => TryNavigateBack() || base.OnBackButtonPressed();

    protected override void OnDisappearing()
    {
        _view.TryCloseQueueEditor();
        if (_view.Panel is TrackLibraryPanel library) library.ResetEditing();
        _forwardNavigation.Clear();
        base.OnDisappearing();
    }

    internal bool TryNavigateBack()
    {
        if (_closed) return false;
        if (_view.Panel is null) return _view.TryNavigateQueueBack();
        if (_view.Panel is TrackLibraryPanel library && (library.TryCloseEditing() || library.TryReturnToSavedLibrary()))
            return true;
        if (_view.Panel is PlayerSettingsPanel settings && settings.TryClosePaletteEditor()) return true;
        ClosePanel();
        return true;
    }

    internal bool TryNavigateForward()
    {
        if (_closed || !_windowVisible || !_forwardNavigation.TryPeek(out var open)) return false;
        _navigatingForward = true;
        try
        {
            if (!open()) return false;
            _forwardNavigation.Pop();
            return true;
        }
        finally { _navigatingForward = false; }
    }

    private void RecordOpeningScreen()
    {
        if (!_navigatingForward) _forwardNavigation.Clear();
    }

    private void RecordClosingPanel(View panel)
    {
        switch (panel)
        {
            case TrackLibraryPanel: _forwardNavigation.Push(OpenTrackLibrary); break;
            case PlayerSettingsPanel: _forwardNavigation.Push(OpenSettings); break;
            case EqualizerPanel: _forwardNavigation.Push(OpenEqualizer); break;
        }
    }

    private void OnQueueEditingChanged(bool editing)
    {
        if (_closed || !_windowVisible || _view.Panel is not null) return;
        if (editing) RecordOpeningScreen();
        else _forwardNavigation.Push(_view.TryOpenQueueEditor);
    }
}
