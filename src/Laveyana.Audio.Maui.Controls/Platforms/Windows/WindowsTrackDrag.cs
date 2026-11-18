using Laveyana.Audio.Playback;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using NativeOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;

namespace Laveyana.Audio.Maui.Controls;

internal static class WindowsTrackDrag
{
    private const string TrackPathFormat = "LavPulse.Mp3Path";

    /// <summary>Publishes the selected track path in the Windows drag payload.</summary>
    public static void AddSource(VisualElement source, Func<TrackFile?> getTrack)
    {
        void OnDragStarting(UIElement sender, Microsoft.UI.Xaml.DragStartingEventArgs args)
        {
            if (getTrack()?.LocalPath is not { } path)
            {
                args.Cancel = true;
                return;
            }

            args.AllowedOperations = NativeOperation.Copy;
            args.Data.SetData(TrackPathFormat, path);
        }

        Attach(source, native =>
        {
            native.CanDrag = true;
            native.DragStarting += OnDragStarting;
        }, native =>
        {
            native.DragStarting -= OnDragStarting;
            native.CanDrag = false;
        });
    }

    /// <summary>Accepts supported files dropped from the file manager or the folder browser.</summary>
    public static void AddTarget(VisualElement target, Func<bool> canDrop,
        Func<IReadOnlyList<TrackFile>, Task> receive, Action<bool>? highlight = null)
    {
        void OnDragOver(object sender, Microsoft.UI.Xaml.DragEventArgs args)
        {
            bool accepted = CanRead(args.DataView) && canDrop();
            args.AcceptedOperation = accepted ? NativeOperation.Copy : NativeOperation.None;
            args.Handled = true; // A deck handles its own drops before they can bubble to the page.
            highlight?.Invoke(accepted);
        }

        void OnDragLeave(object sender, Microsoft.UI.Xaml.DragEventArgs args)
            => highlight?.Invoke(false);

        // Keep the native payload alive until its storage items have been read.
        async void OnDrop(object sender, Microsoft.UI.Xaml.DragEventArgs args)
        {
            highlight?.Invoke(false);
            args.Handled = true;
            if (!canDrop() || !CanRead(args.DataView))
            {
                args.AcceptedOperation = NativeOperation.None;
                return;
            }

            var deferral = args.GetDeferral();
            IReadOnlyList<TrackFile> files = [];
            try
            {
                if (args.DataView.Contains(TrackPathFormat))
                {
                    var path = await args.DataView.GetDataAsync(TrackPathFormat) as string;
                    if (!string.IsNullOrEmpty(path) && TrackFile.IsMp3(path))
                    {
                        files = [TrackFile.FromPath(path)];
                    }
                }
                else
                {
                    files = (await args.DataView.GetStorageItemsAsync()).OfType<StorageFile>()
                        .Where(file => TrackFile.IsMp3(file.Name) && !string.IsNullOrEmpty(file.Path))
                        .Select(file => TrackFile.FromPath(file.Path))
                        .ToArray();
                }

                args.AcceptedOperation = files.Count == 0 ? NativeOperation.None : NativeOperation.Copy;
            }
            catch (Exception ex)
            {
                args.AcceptedOperation = NativeOperation.None;
                System.Diagnostics.Debug.WriteLine(ex);
            }
            finally
            {
                deferral.Complete();
            }

            await receive(files);
        }

        Attach(target, native =>
        {
            native.AllowDrop = true;
            native.DragOver += OnDragOver;
            native.DragLeave += OnDragLeave;
            native.Drop += OnDrop;
        }, native =>
        {
            native.DragOver -= OnDragOver;
            native.DragLeave -= OnDragLeave;
            native.Drop -= OnDrop;
            native.AllowDrop = false;
        });
    }

    /// <summary>Checks if the native payload contains a track path or storage items.</summary>
    private static bool CanRead(Windows.ApplicationModel.DataTransfer.DataPackageView data)
        => data.Contains(TrackPathFormat) || data.Contains(StandardDataFormats.StorageItems);

    /// <summary>Wires the native drag events for the current handler and unhooks replaced handlers.</summary>
    private static void Attach(VisualElement view, Action<FrameworkElement> attach, Action<FrameworkElement> detach)
    {
        if (view.Handler?.PlatformView is FrameworkElement current)
        {
            attach(current);
        }

        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is FrameworkElement next)
            {
                attach(next);
            }
        };
        view.HandlerChanging += (_, args) =>
        {
            if (args.OldHandler?.PlatformView is FrameworkElement previous)
            {
                detach(previous);
            }
        };
    }
}
