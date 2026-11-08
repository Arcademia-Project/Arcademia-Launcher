using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ArcademiaGameLauncher.Utils
{
    public readonly record struct Int32Rect(int X, int Y, int Width, int Height);

    public class VisibilityExtensions : AvaloniaObject
    {
        public static readonly AttachedProperty<bool> IsHiddenProperty =
            AvaloniaProperty.RegisterAttached<VisibilityExtensions, Visual, bool>("IsHidden");

        private static readonly AttachedProperty<double> OpacityBeforeHiddenProperty =
            AvaloniaProperty.RegisterAttached<VisibilityExtensions, Visual, double>("OpacityBeforeHidden", 1d);

        static VisibilityExtensions()
        {
            IsHiddenProperty.Changed.AddClassHandler<Visual>((visual, e) =>
            {
                if (e.GetNewValue<bool>())
                {
                    visual.SetValue(OpacityBeforeHiddenProperty, visual.Opacity);
                    visual.Opacity = 0;
                    if (visual is InputElement input)
                        input.IsHitTestVisible = false;
                }
                else
                {
                    visual.Opacity = visual.GetValue(OpacityBeforeHiddenProperty);
                    if (visual is InputElement input)
                        input.IsHitTestVisible = true;
                }
            });
        }

        public static bool GetIsHidden(Visual visual) => visual.GetValue(IsHiddenProperty);

        public static void SetIsHidden(Visual visual, bool value) => visual.SetValue(IsHiddenProperty, value);
    }

    public static class VisualCompat
    {
        public static bool IsHidden(this Visual visual) => VisibilityExtensions.GetIsHidden(visual);

        public static void SetHidden(this Visual visual, bool hidden) => VisibilityExtensions.SetIsHidden(visual, hidden);
    }

    public static class TextMetrics
    {
        public static double? GlyphHeight(Typeface typeface)
        {
            if (!FontManager.Current.TryGetGlyphTypeface(typeface, out var glyph))
                return null;

            var metrics = glyph.Metrics;
            return metrics.DesignEmHeight == 0
                ? null
                : (metrics.Descent - metrics.Ascent) / (double)metrics.DesignEmHeight;
        }
    }
}
