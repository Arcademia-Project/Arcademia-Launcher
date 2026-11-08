using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ArcademiaGameLauncher.Utils
{
    public static class TextBlockExtensions
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

            public override bool Equals(object obj) => Equals(obj as FitKey);

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

        /// <summary>
        /// Adjusts the FontSize of <paramref name="textBlock"/> so that
        /// <paramref name="desiredText"/> fits within its ActualWidth/ActualHeight,
        /// wrapping into at most <paramref name="maxLines"/> lines.
        /// </summary>
        public static void FitTextToTextBlock(
            this TextBlock textBlock,
            string desiredText,
            double targetFontSize,
            int maxLines = 100,
            double minFontSize = 8.0,
            double precision = 0.5
        )
        {
            if (textBlock == null || string.IsNullOrWhiteSpace(desiredText))
                return;

            if (textBlock.Bounds.Width <= 0 || textBlock.Bounds.Height <= 0)
            {
                void Handler(object s, SizeChangedEventArgs e)
                {
                    if (textBlock.Bounds.Width > 0 && textBlock.Bounds.Height > 0)
                    {
                        textBlock.SizeChanged -= Handler;
                        textBlock.FitTextToTextBlock(
                            desiredText,
                            targetFontSize,
                            maxLines,
                            minFontSize,
                            precision
                        );
                    }
                }
                textBlock.SizeChanged += Handler;
                return;
            }

            var key = new FitKey(
                desiredText,
                textBlock.Bounds.Width,
                textBlock.Bounds.Height,
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch,
                maxLines,
                targetFontSize,
                minFontSize,
                precision
            );

            if (_cache.TryGetValue(key, out double cachedSize))
            {
                textBlock.Text = desiredText;
                textBlock.TextWrapping = TextWrapping.Wrap;
                textBlock.FontSize = Math.Max(cachedSize, minFontSize);
                return;
            }

            double availableWidth = textBlock.Bounds.Width;
            double availableHeight = textBlock.Bounds.Height;

            Typeface typeface = new Typeface(
                textBlock.FontFamily,
                textBlock.FontStyle,
                textBlock.FontWeight,
                textBlock.FontStretch
            );

            var glyphHeight = TextMetrics.GlyphHeight(typeface);

            double GetLineHeight(double fontSize)
            {
                if (glyphHeight is double height)
                    return height * fontSize;
                else
                    return fontSize;
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
                    Trimming = TextTrimming.None,
                    TextAlignment = textBlock.TextAlignment,
                };

                double totalHeight = ft.Height;
                double lineH = GetLineHeight(fontSize);

                int linesUsed = (int)Math.Ceiling(totalHeight / lineH);

                bool fitsLines = linesUsed <= maxLines;
                bool fitsHeight = totalHeight <= availableHeight;

                return fitsLines && fitsHeight;
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

            textBlock.Text = desiredText;
            textBlock.TextWrapping = TextWrapping.Wrap;
            textBlock.FontSize = bestSize;
            textBlock.LineHeight = GetLineHeight(bestSize * 1.2);
        }
    }
}
