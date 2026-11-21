using System.Runtime.InteropServices;

#if ANDROID
using Android.Graphics;
#elif WINDOWS
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
#else
using Microsoft.Maui.Graphics.Platform;
#endif

namespace Laveyana.LavPulse.Tracks;

/// <summary>Creates a small queue image separately from the cover displayed on the deck.</summary>
internal static class ArtworkThumbnail
{
    private const int MaxPixels = 128;

    public static Task<ReadOnlyMemory<byte>> CreateAsync(ReadOnlyMemory<byte> artwork, CancellationToken token) =>
        Task.Run(async () =>
        {
            try
            {
                return await ResizeAsync(artwork).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Damaged embedded graphics should not prevent a track from appearing or playing.
                System.Diagnostics.Debug.WriteLine(ex);
                return ReadOnlyMemory<byte>.Empty;
            }
        }, token);

    private static MemoryStream Open(ReadOnlyMemory<byte> artwork)
    {
        var bytes = MemoryMarshal.TryGetArray(artwork, out var segment) ? segment : new ArraySegment<byte>(artwork.ToArray());
        return new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false);
    }

#if ANDROID
    private static Task<ReadOnlyMemory<byte>> ResizeAsync(ReadOnlyMemory<byte> artwork)
    {
        var bytes = MemoryMarshal.TryGetArray(artwork, out var segment) ? segment : new ArraySegment<byte>(artwork.ToArray());
        using var options = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(bytes.Array!, bytes.Offset, bytes.Count, options)?.Dispose();
        int largest = Math.Max(options.OutWidth, options.OutHeight);
        if (largest <= 0) return Task.FromResult(ReadOnlyMemory<byte>.Empty);
        if (largest <= MaxPixels) return Task.FromResult(artwork);

        int sample = 1;
        while (largest / (sample * 2) >= MaxPixels) sample *= 2;
        options.InJustDecodeBounds = false;
        options.InSampleSize = sample;
        using var bitmap = BitmapFactory.DecodeByteArray(bytes.Array!, bytes.Offset, bytes.Count, options);
        if (bitmap is null) return Task.FromResult(ReadOnlyMemory<byte>.Empty);

        double scale = Math.Min(1, MaxPixels / (double)Math.Max(bitmap.Width, bitmap.Height));
        using var scaled = scale < 1 ? Bitmap.CreateScaledBitmap(bitmap, Math.Max(1, (int)(bitmap.Width * scale)),
            Math.Max(1, (int)(bitmap.Height * scale)), true) : null;
        using var output = new MemoryStream();
        var format = bitmap.HasAlpha ? Bitmap.CompressFormat.Png! : Bitmap.CompressFormat.Jpeg!;
        return Task.FromResult((scaled ?? bitmap).Compress(format, 82, output)
            ? (ReadOnlyMemory<byte>)output.ToArray() : ReadOnlyMemory<byte>.Empty);
    }
#elif WINDOWS
    private static async Task<ReadOnlyMemory<byte>> ResizeAsync(ReadOnlyMemory<byte> artwork)
    {
        using var input = Open(artwork);
        using var randomInput = input.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomInput);
        uint largest = Math.Max(decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        if (largest == 0) return ReadOnlyMemory<byte>.Empty;
        if (largest <= MaxPixels) return artwork;

        double scale = MaxPixels / (double)largest;
        // The transform is expressed in the bitmap's original coordinate system.
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1u, (uint)(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1u, (uint)(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        using var randomOutput = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomOutput);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        randomOutput.Seek(0);
        using var output = randomOutput.AsStreamForRead();
        var thumbnail = new byte[checked((int)randomOutput.Size)];
        await output.ReadExactlyAsync(thumbnail);
        return thumbnail;
    }
#else
    private static Task<ReadOnlyMemory<byte>> ResizeAsync(ReadOnlyMemory<byte> artwork)
    {
        using var input = Open(artwork);
        using var image = PlatformImage.FromStream(input);
        if (image is null) return Task.FromResult(ReadOnlyMemory<byte>.Empty);
        if (Math.Max(image.Width, image.Height) <= MaxPixels) return Task.FromResult(artwork);

        using var thumbnail = image.Downsize(MaxPixels, disposeOriginal: false);
        using var output = new MemoryStream();
        thumbnail.Save(output, Microsoft.Maui.Graphics.ImageFormat.Png);
        return Task.FromResult<ReadOnlyMemory<byte>>(output.ToArray());
    }
#endif
}
