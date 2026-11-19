using Microsoft.Maui.Platform;
using NativeButton = Microsoft.UI.Xaml.Controls.Button;
using NativeImage = Microsoft.UI.Xaml.Controls.Image;

namespace Laveyana.Audio.Maui.Controls;

internal static partial class MixerStyle
{
    static partial void SetNativeButtonIconSize(View button)
    {
        button.HandlerChanged += (_, _) =>
        {
            if (button.Handler?.PlatformView is NativeButton native && native.GetContent<NativeImage>() is { } image)
            {
                // MAUI sets the bitmap's maximum height after ImageOpened. Bound the image before its first layout.
                image.Width = ButtonIconSize;
                image.Height = ButtonIconSize;
            }
        };
    }
}
