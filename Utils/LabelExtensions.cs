using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ArcademiaGameLauncher.Utils
{
    public static class LabelExtensions
    {
        private class FitKey(
            string text,
            double width,
            double height,
            FontFamily family,
            FontStyle style,
            FontWeight weight,
            FontStretch stretch,
            int maxLines,
            double targetSize,
            double minSize,
            double precision
        ) : IEquatable<FitKey>
        {
            public string Text { get; } = text;
            public double Width { get; } = width;
            public double Height { get; } = height;
            public FontFamily Family { get; } = family;
            public FontStyle Style { get; } = style;
            public FontWeight Weight { get; } = weight;
            public FontStretch Stretch { get; } = stretch;
            public int MaxLines { get; } = maxLines;
            public double TargetSize { get; } = targetSize;
            public double MinSize { get; } = minSize;
            public double Precision { get; } = precision;

            public bool Equals(FitKey other)
            {
                if (other == null)
                    return false;
                return Text == other.Text
                    && Width == other.Width
                    && Height == other.Height
                    && Family == other.Family
                    && Style == other.Style
                    && Weight == other.Weight
                    && Stretch == other.Stretch
                    && MaxLines == other.MaxLines
                    && TargetSize == other.TargetSize
                    && MinSize == other.MinSize
                    && Precision == other.Precision;
            }

            public override bool Equals(object obj) => Equals(obj as FitKey);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Text?.GetHashCode() ?? 0;
                    hash = hash * 397 ^ Width.GetHashCode();
                    hash = hash * 397 ^ Height.GetHashCode();
                    hash = hash * 397 ^ (Family?.GetHashCode() ?? 0);
                    hash = hash * 397 ^ Style.GetHashCode();
                    hash = hash * 397 ^ Weight.GetHashCode();
                    hash = hash * 397 ^ Stretch.GetHashCode();
                    hash = hash * 397 ^ MaxLines;
                    hash = hash * 397 ^ TargetSize.GetHashCode();
                    hash = hash * 397 ^ MinSize.GetHashCode();
                    hash = hash * 397 ^ Precision.GetHashCode();
                    return hash;
                }
            }
        }

        private static readonly Dictionary<FitKey, double> _cache = [];

        public static void FitTextToLabel(
            this Label label,
            string desiredText,
            double targetFontSize,
            int maxLines = 100,
            double minFontSize = 8.0,
            double precision = 0.5
        )
        {
            if (label == null || string.IsNullOrWhiteSpace(desiredText))
                return;

            if (label.Bounds.Width <= 0 || label.Bounds.Height <= 0)
            {
                void Handler(object s, SizeChangedEventArgs e)
                {
                    if (label.Bounds.Width > 0 && label.Bounds.Height > 0)
                    {
                        label.SizeChanged -= Handler;
                        label.FitTextToLabel(
                            desiredText,
                            targetFontSize,
                            maxLines,
                            minFontSize,
                            precision
                        );
                    }
                }

                label.SizeChanged += Handler;
                return;
            }

            var key = new FitKey(
                desiredText,
                label.Bounds.Width,
                label.Bounds.Height,
                label.FontFamily,
                label.FontStyle,
                label.FontWeight,
                label.FontStretch,
                maxLines,
                targetFontSize,
                minFontSize,
                precision
            );

            if (_cache.TryGetValue(key, out double cachedSize))
            {
                label.Content = desiredText;
                label.FontSize = Math.Max(cachedSize, minFontSize);
                return;
            }

            var padding = label.Padding;
            var border = label.BorderThickness;

            double availableWidth = Math.Max(
                0,
                label.Bounds.Width - padding.Left - padding.Right - border.Left - border.Right
            );

            double availableHeight = Math.Max(
                0,
                label.Bounds.Height - padding.Top - padding.Bottom - border.Top - border.Bottom
            );

            Typeface typeface = new Typeface(
                label.FontFamily,
                label.FontStyle,
                label.FontWeight,
                label.FontStretch
            );

            var glyphHeight = TextMetrics.GlyphHeight(typeface);

            double GetLineHeight(double fontSize)
            {
                return glyphHeight is double height ? height * fontSize : fontSize;
            }

            bool TextFits(double fontSize)
            {

                var ft = new FormattedText(
                    desiredText,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.Black
                )
                {
                    MaxTextWidth = availableWidth,
                    TextAlignment = TextAlignment.Center,
                };

                double totalHeight = ft.Height;
                double lineHeight = GetLineHeight(fontSize);

                int linesUsed = (int)Math.Ceiling(totalHeight / lineHeight);

                return linesUsed <= maxLines && totalHeight <= availableHeight;
            }

            double bestSize = minFontSize;

            if (TextFits(targetFontSize))
            {
                bestSize = targetFontSize;
            }
            else
            {
                double low = minFontSize;
                double high = targetFontSize;

                while (high - low > precision)
                {
                    double mid = (low + high) / 2.0;
                    if (TextFits(mid))
                    {
                        bestSize = mid;
                        low = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                }
            }

            bestSize = Math.Max(bestSize, minFontSize);

            _cache[key] = bestSize;

            // If it's at minimum size and text still doesn't fit, add ellipsis
            if (Math.Abs(bestSize - minFontSize) < 0.01)
            {
                var ft = new FormattedText(
                    desiredText,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    bestSize,
                    Brushes.Black
                )
                {
                    MaxTextWidth = availableWidth,
                };

                bool stillTooTall = ft.Height > availableHeight;

                if (stillTooTall)
                {
                    desiredText = TrimToEllipsis(
                        desiredText,
                        availableWidth,
                        availableHeight,
                        typeface,
                        bestSize
                    );
                }
            }

            label.Content = desiredText;
            label.FontSize = bestSize;
        }

        private static string TrimToEllipsis(
            string text,
            double width,
            double height,
            Typeface typeface,
            double fontSize
        )
        {
            if (string.IsNullOrEmpty(text))
                return text;

            string working = text;

            while (working.Length > 1)
            {
                string test = working + "...";

                var ft = new FormattedText(
                    test,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    Brushes.Black
                )
                {
                    MaxTextWidth = width,
                };

                if (ft.Height <= height)
                    return test;

                working = working.Substring(0, working.Length - 1);
            }

            return "..."; // worst-case scenario
        }
    }
}
