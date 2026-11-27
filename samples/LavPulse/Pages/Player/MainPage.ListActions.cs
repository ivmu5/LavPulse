using Laveyana.Audio.Playback;
using Laveyana.LavPulse.Controls;

namespace Laveyana.LavPulse;

public sealed partial class MainPage
{
    private const string OpenLocation="Показать в проводнике";
    private const string RemoveCatalog="Убрать из медиатеки";
    private const string RemoveStorage="Удалить копию из хранилища";
    private const string ClearDerived="Очистить данные анализа";
    private const string Rescan="Очистить и пересканировать";
    private const string AddQueue="Добавить в очередь";
    private const string RemoveQueue="Убрать из очереди";
    private const string NextQueue="Воспроизвести следующим";
    private const string PlayNow="Воспроизвести сейчас";

    private bool InQueue(TrackFile file)=>_queue.Tracks.Any(t=>SameMedia(t,file));
    private static bool SameMedia(TrackFile a,TrackFile b)=>a.LocalPath is { } x && b.LocalPath is { } y
        ? string.Equals(x,y,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)
        : a==b;
    private TrackFile? QueueFile(TrackFile file)=>_queue.Tracks.FirstOrDefault(t=>SameMedia(t,file));
    private bool Pinned(TrackFile file)
    {
        var actual=_playback.Snapshot;
        var presented=_playback.PresentedSnapshot;
        foreach(var track in new[]{actual.Current?.Track,actual.Incoming?.Track,presented.Current?.Track,
            presented.Incoming?.Track,_shownCurrent,_shownIncoming})
            if(track is not null && _library.Find(track) is { } source && SameMedia(source,file)) return true;
        return false;
    }

    private async Task ShowQueueActionsAsync(IReadOnlyList<TrackFile> files)
    {
        if(_closed||files.Count==0) return;
        var options=new List<string>{PlayNow,NextQueue,RemoveQueue,ClearDerived,Rescan,RemoveCatalog,RemoveStorage};
        if(files.Count!=1) options.Remove(PlayNow);
#if WINDOWS
        if(files.Count==1 && files[0].LocalPath is not null) options.Insert(0,OpenLocation);
#endif
        var choice=await DisplayActionSheetAsync(files.Count==1?files[0].Name:$"Выбрано: {files.Count}","Отмена",null,options.ToArray());
        if(_closed||choice is null||!options.Contains(choice)) return;
        await RunListActionAsync(async ()=>
        {
            if(choice==OpenLocation) { RevealPath(files[0].LocalPath!); return; }
            if(choice==PlayNow) { await ChooseAsync(files[0],autoPlay:true); return; }
            if(choice==NextQueue) { await PlayNextAsync(files); return; }
            if(choice==RemoveQueue) { foreach(var file in files) await RemoveQueueTrackAsync(file); return; }
            if(choice==RemoveCatalog)
            {
                await _importGate.WaitAsync(_lifetime.Token);
                try
                {
                    foreach(var file in files) _trackStore.ForgetAllLibraryMemberships(file);
                    await SaveQueueAsync();
                }
                finally {_importGate.Release();}
                return;
            }
            await ChangeStoredFilesAsync(files,choice);
        });
    }

    private async Task ShowLibraryActionsAsync(IReadOnlyList<TrackLibrarySelection> selections)
    {
        if(_closed||selections.Count==0) return;
        var files=await SelectionFilesAsync(selections);
        var options=new List<string>();
        if(files.Any(f=>!InQueue(f))) options.Add(AddQueue);
        if(files.Any(InQueue)) options.Add(RemoveQueue);
        if(selections.All(s=>s.Local))
        {
            options.AddRange([ClearDerived,Rescan,RemoveCatalog,RemoveStorage]);
            if(files.Count==1) options.Insert(0,PlayNow);
            if(files.Count>0) options.Insert(0,NextQueue);
        }
#if WINDOWS
        if(selections.Count==1) options.Insert(0,OpenLocation);
#endif
        if(options.Count==0) return;
        string? choice=await DisplayActionSheetAsync(selections.Count==1?selections[0].File?.Name??selections[0].Folders[^1].Name:
            $"Выбрано: {selections.Count}","Отмена",null,options.ToArray());
        if(_closed||choice is null||!options.Contains(choice)) return;
        if(choice==OpenLocation)
        {
            var selection=selections[0];
            RevealPath(selection.File?.LocalPath??(selection.Local?_trackStore.MediaFolder:selection.Folders[^1].Id));
            return;
        }
        if(choice==AddQueue||choice==NextQueue||choice==PlayNow)
        {
            foreach(var selection in selections) await ImportLibrarySelectionAsync(selection,true);
            var queued=files.Select(QueueFile).OfType<TrackFile>().ToArray();
            if(choice==NextQueue) await PlayNextAsync(queued);
            if(choice==PlayNow && queued.Length==1) await ChooseAsync(queued[0],autoPlay:true);
            return;
        }
        if(choice==RemoveQueue)
        {
            foreach(var file in files.Select(QueueFile).OfType<TrackFile>().ToArray()) await RemoveQueueTrackAsync(file);
            return;
        }
        if(choice==RemoveCatalog)
        {
            foreach(var selection in selections) await RemoveLibrarySelectionAsync(selection);
            return;
        }
        await ChangeStoredFilesAsync(files,choice);
    }

    private async Task<IReadOnlyList<TrackFile>> SelectionFilesAsync(IReadOnlyList<TrackLibrarySelection> selections)
    {
        var result=new List<TrackFile>();
        foreach(var selection in selections)
            foreach(var file in await ListLibraryScanTracksAsync(selection,_lifetime.Token))
                if(!result.Any(f=>SameMedia(f,file))) result.Add(file);
        return result;
    }

    private Task ExecuteQueueSwipeAsync(TrackFile file,ListSwipeAction action)=>RunListActionAsync(async ()=>
    {
        switch(action)
        {
            case ListSwipeAction.PlayNext: await PlayNextAsync([file]); break;
            case ListSwipeAction.RemoveFromQueue: await RemoveQueueTrackAsync(file); break;
            case ListSwipeAction.ClearAnalysis: await ChangeStoredFilesAsync([file],ClearDerived); break;
            case ListSwipeAction.Rescan: await ChangeStoredFilesAsync([file],Rescan); break;
            case ListSwipeAction.RemoveFromStorage: await ChangeStoredFilesAsync([file],RemoveStorage); break;
            case ListSwipeAction.AddToQueue: break;
        }
    });

    private async Task ExecuteLibrarySwipeAsync(TrackLibrarySelection selection,ListSwipeAction action)
    {
        if(action==ListSwipeAction.AddToQueue) { await ImportLibrarySelectionAsync(selection,true); return; }
        var files=await SelectionFilesAsync([selection]);
        if(action==ListSwipeAction.PlayNext)
        {
            await ImportLibrarySelectionAsync(selection,true);
            await PlayNextAsync(files.Select(QueueFile).OfType<TrackFile>().ToArray());
        }
        else if(action==ListSwipeAction.RemoveFromQueue)
        {
            foreach(var file in files.Select(QueueFile).OfType<TrackFile>().ToArray()) await RemoveQueueTrackAsync(file);
        }
        else if(selection.Local && action is ListSwipeAction.ClearAnalysis or ListSwipeAction.Rescan or ListSwipeAction.RemoveFromStorage)
            await ChangeStoredFilesAsync(files,action==ListSwipeAction.ClearAnalysis?ClearDerived:action==ListSwipeAction.Rescan?Rescan:RemoveStorage);
    }

    private async Task PlayNextAsync(IReadOnlyList<TrackFile> files)
    {
        if(_closed||IsTransitioning(_playback.Snapshot)||IsStopping) return;
        // In reverse order, inserting each immediately after current preserves the selection order.
        foreach(var file in files.Reverse())
        {
            if(ReferenceEquals(file,_queue.Current)) continue;
            if(_queue.Current is { } current && _queue.Contains(current))
                await MoveQueueAsync(file,current,true);
            else if(_queue.Next is { } next && !ReferenceEquals(file,next))
                await MoveQueueAsync(file,next,false);
        }
    }

    private async Task ChangeStoredFilesAsync(IReadOnlyList<TrackFile> files,string action)
    {
        bool remove=action==RemoveStorage, rescan=action==Rescan;
        if(remove && !await DisplayAlertAsync("Удалить из хранилища?",
            "Будут удалены импортированные копии и их данные анализа. Исходные файлы вне LavPulse не затрагиваются. Треки в очереди или используемые декой будут пропущены.",
            "Удалить","Отмена")) return;
        int done=0, skipped=0;
        foreach(var file in files)
        {
            if(_closed) break;
            if(Pinned(file)||(remove&&InQueue(file))) { skipped++; continue; }
            try
            {
                if(remove) await _importGate.WaitAsync(_lifetime.Token);
                try { await _library.ClearDerivedAsync(file,remove,_lifetime.Token); }
                finally {if(remove)_importGate.Release();}
                if(rescan) await ScanLibraryTrackAsync(file,true,_lifetime.Token);
                done++;
            }
            catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) {break;}
            catch(Exception ex) {skipped++; System.Diagnostics.Debug.WriteLine(ex);}
        }
        if(remove&&!_closed) await SaveQueueAsync();
        if(!_closed)
        {
            UpdateQueue();
            await DisplayAlertAsync(action,$"Выполнено: {done}. Пропущено или ошибок: {skipped}.","OK");
        }
    }

    private async Task RunListActionAsync(Func<Task> action)
    {
        try {await action();}
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested){}
        catch(Exception ex) {if(!_closed)_view.Status.Text=ex.Message;}
    }

    private static void RevealPath(string path)
    {
#if WINDOWS
        if(!Path.IsPathFullyQualified(path)) return;
        if(!File.Exists(path)&&!Directory.Exists(path)) return;
        var start=new System.Diagnostics.ProcessStartInfo("explorer.exe"){UseShellExecute=true};
        start.Arguments=$"/select,\"{path}\"";
        System.Diagnostics.Process.Start(start)?.Dispose();
#endif
    }
}
