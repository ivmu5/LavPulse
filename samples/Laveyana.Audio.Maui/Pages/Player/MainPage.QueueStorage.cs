using Laveyana.Audio.Maui.Tracks;

namespace Laveyana.Audio.Maui;

public sealed partial class MainPage
{
    private bool _localQueueReady;
    private Task? _restoreQueue;
    private Task _saveQueue = Task.CompletedTask;

    private async Task RestoreLocalQueueAsync()
    {
        var token = _lifetime.Token;
        try
        {
            var restored = await Task.Run(() => _trackStore.ReadQueueAsync(token), token);
            if (_closed) return;
            _queue.AddRange(_library.Add(restored.Tracks, []));
            _analysisQueue = _queue.Tracks.ToArray();
            _localQueueReady = true;
            UpdateQueue();
            if (_queue.Tracks.Count > 0) _ = LoadFirstAsync(restored.Current);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Не удалось восстановить очередь. {ex.Message}";
        }
        finally
        {
            _localQueueReady = !_closed;
            ProcessSystemTrackRequests();
        }
    }

    private Task SaveQueueAsync()
    {
        var current = _queue.Current;
        if (current is not null && !_queue.Contains(current)) current = _queue.Next;
        _analysisQueue = _queue.Tracks.ToArray();
        var snapshot = _trackStore.Snapshot(_analysisQueue, current);
        return _saveQueue = SaveAfterAsync(_saveQueue, snapshot);
    }

    private async Task SaveAfterAsync(Task previous, StoredQueue snapshot)
    {
        try
        {
            await previous;
            await Task.Run(() => _trackStore.SaveQueueAsync(snapshot));
        }
        catch (Exception ex)
        {
            if (!_closed) _view.Status.Text = $"Не удалось сохранить очередь. {ex.Message}";
        }
    }
}
