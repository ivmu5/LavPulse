#if !ANDROID && !WINDOWS
namespace Laveyana.Audio.Maui.Controls;

internal static class NumericInputGestures
{
    /// <summary>Provides the standard MAUI double tap on platforms without a native input adapter.</summary>
    public static void Attach(View view, Func<double, bool> scroll, Action? reset = null)
    {
        if (reset is not null)
        {
            var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
            doubleTap.Tapped += (_, _) => reset();
            view.GestureRecognizers.Add(doubleTap);
        }
    }
}
#endif
