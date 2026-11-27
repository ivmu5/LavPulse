using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Laveyana.LavPulse.Controls;

internal static partial class MixerStyle
{
    // Each live button tracks only its own background; no global event or
    // reference to recycled rows. A source is replaced only when its variant changes.
    private sealed class ContrastBinding
    {
        public string? File;
        public BindableProperty? Property;
        public bool ThemeFallback;
        public bool ReadableText;
    }

    private static readonly ConditionalWeakTable<VisualElement, ContrastBinding> ContrastBindings = new();

    private static ContrastBinding BindContrast(VisualElement view)
    {
        return ContrastBindings.GetValue(view, target =>
        {
            var binding = new ContrastBinding();
            target.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(VisualElement.BackgroundColor))
                    RefreshContrast(target, binding);
            };
            return binding;
        });
    }

    private static void RefreshContrast(VisualElement view, ContrastBinding binding)
    {
        // Transparent controls without their own surface retain their theme
        // resource. The animated swipe supplies its composite background explicitly.
        if (view.BackgroundColor is not { Alpha: >= .999f } background)
        {
            if (binding.File is { } fallback && binding.Property is { } property && !binding.ThemeFallback)
            {
                binding.ThemeFallback = true;
                view.SetDynamicResource(property, IconKey(fallback));
            }
            if (binding.ReadableText && view is Button transparentButton)
                UseColor(transparentButton, Button.TextColorProperty, MixerColor.Text);
            return;
        }

        double luminance = IconContrast.Luminance(background.Red, background.Green, background.Blue);
        if (binding.File is { } file && binding.Property is { } iconProperty)
        {
            if (binding.ThemeFallback) view.RemoveDynamicResource(iconProperty);
            binding.ThemeFallback = false;
            SetContrastIcon(view, iconProperty, file, luminance);
        }
        if (binding.ReadableText && view is Button button)
        {
            button.RemoveDynamicResource(Button.TextColorProperty);
            SetContrastText(button, luminance);
        }
    }

    private static bool TryBindContrastIcon(VisualElement view, BindableProperty property, string file)
    {
        if (view is not (Button or ImageButton) || !file.StartsWith("player_", StringComparison.Ordinal) ||
            !file.EndsWith(".png", StringComparison.Ordinal) ||
            !Icons.Contains(file[7..^4], StringComparer.Ordinal)) return false;
        var binding = BindContrast(view);
        if (binding.File != file || binding.Property != property)
        {
            binding.File = file;
            binding.Property = property;
            binding.ThemeFallback = false;
        }
        RefreshContrast(view, binding);
        return true;
    }

    internal static void UseReadableButtonText(Button button)
    {
        var binding = BindContrast(button);
        binding.ReadableText = true;
        RefreshContrast(button, binding);
    }

    internal static void SetContrastIcon(VisualElement view, BindableProperty property, string file, double luminance)
    {
        bool dark = IconContrast.DarkIcon(luminance, file);
        bool suffix = IconContrast.UseLightSuffix(file, dark);
        if (view.GetValue(property) is FileImageSource { File: { } current })
        {
            // Avoid even allocating a variant filename on stable swipe frames.
            if (!suffix && current == file) return;
            if (suffix && current.Length == file.Length + 6 &&
                current.AsSpan().StartsWith(file.AsSpan(0, file.Length - 4), StringComparison.Ordinal) &&
                current.EndsWith("_light.png", StringComparison.Ordinal)) return;
        }
        string variant = suffix ? file[..^4] + "_light.png" : file;
        view.RemoveDynamicResource(property);
        view.SetValue(property, new FileImageSource { File = variant });
    }

    internal static void SetContrastText(Button button, double luminance)
    {
        Color color = IconContrast.DarkText(luminance) ? Colors.Black : Colors.White;
        if (button.TextColor != color) button.TextColor = color;
    }
}
