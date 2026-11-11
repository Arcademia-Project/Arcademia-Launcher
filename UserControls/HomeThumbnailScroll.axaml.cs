using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ArcademiaGameLauncher.Utils;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ArcademiaGameLauncher.UserControls
{
    public sealed class ThumbnailEntry
    {
        public readonly Bitmap StaticSource;
        public readonly string TempFilePath;
        public readonly bool IsGif;

        public ThumbnailEntry(Bitmap staticImage) => StaticSource = staticImage;

        public ThumbnailEntry(Bitmap firstFrame, string tempFilePath)
        {
            StaticSource = firstFrame;
            TempFilePath = tempFilePath;
            IsGif = true;
        }
    }

    public partial class HomeThumbnailScroll : UserControl
    {
        private const double DisplayWidth = 1320;
        private const double DisplayHeight = 1080;

        // 7:3 thumbnail ratio
        private const double ThumbW = 350;
        private const double ThumbH = 150;

        private const double GapX = 15;
        private const double GapY = 15;
        private const double CellW = ThumbW + GapX;
        private const double CellH = ThumbH + GapY;

        private const int NumRows = 14;
        private const double ScrollPixelsPerSecond = 30.0;

        private static readonly double RowWidth =
            Math.Sqrt(DisplayWidth * DisplayWidth + DisplayHeight * DisplayHeight) + 2 * CellW;

        private sealed class Tile
        {
            public Border Border;
            public ThumbnailEntry Entry;
            public Image Master;
            public EventHandler<AvaloniaPropertyChangedEventArgs> FrameHandler;
        }

        private sealed class Row
        {
            public StackPanel Panel;
            public TranslateTransform Shift;
            public readonly List<Tile> Tiles = new();
            public double Start;
            public int Next;
        }

        private readonly List<Row> _rows = new();
        private readonly Dictionary<ThumbnailEntry, (Image Master, int Uses)> _gifMasters = new();
        private List<ThumbnailEntry> _entries = new();
        private long _scrollStarted;
        private bool _frameRequested;

        public HomeThumbnailScroll()
        {
            InitializeComponent();

            RowsContainer.Width = RowWidth;
            RowsContainer.Height = NumRows * CellH;
            Canvas.SetLeft(RowsContainer, (DisplayWidth - RowWidth) / 2.0);
            Canvas.SetTop(RowsContainer, (DisplayHeight - NumRows * CellH) / 2.0);
        }

        private double Travelled =>
            Stopwatch.GetElapsedTime(_scrollStarted).TotalSeconds * ScrollPixelsPerSecond;

        public void StopAndClear()
        {
            foreach (var row in _rows)
                foreach (var tile in row.Tiles)
                    Unhook(tile);
            _rows.Clear();
            RowsContainer.Children.Clear();

            foreach (var (master, _) in _gifMasters.Values)
                AnimationBehavior.SetSourceUri(master, null);
            _gifMasters.Clear();
            GifMasterContainer.Children.Clear();

            _entries = new List<ThumbnailEntry>();
        }

        public void LoadThumbnails(IReadOnlyList<ThumbnailEntry> images)
        {
            if (images == null || images.Count == 0)
            {
                StopAndClear();
                return;
            }

            _entries = images.ToList();
            ReleaseUnusedMasters();

            if (_rows.Count == 0)
            {
                _scrollStarted = Stopwatch.GetTimestamp();
                BuildRows();
            }

            var travelled = Travelled;
            foreach (var row in _rows)
                Advance(row, travelled);

            RequestScrollFrame();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestScrollFrame();
        }

        private void BuildRows()
        {
            int[] offsets = BuildShuffledOffsets(NumRows, _entries.Count);

            for (int i = 0; i < NumRows; i++)
            {
                var shift = new TranslateTransform();
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, GapY),
                    RenderTransform = shift,
                };

                _rows.Add(new Row { Panel = panel, Shift = shift, Next = offsets[i] });
                RowsContainer.Children.Add(panel);
            }
        }

        private void RequestScrollFrame()
        {
            if (_frameRequested || _rows.Count == 0)
                return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
                return;

            _frameRequested = true;
            topLevel.RequestAnimationFrame(OnScrollFrame);
        }

        private void OnScrollFrame(TimeSpan _)
        {
            _frameRequested = false;
            if (_rows.Count == 0)
                return;

            var travelled = Travelled;
            foreach (var row in _rows)
                Advance(row, travelled);

            RequestScrollFrame();
        }

        private void Advance(Row row, double travelled)
        {
            while (row.Tiles.Count > 0 && row.Start - travelled + CellW < 0)
            {
                RemoveFirstTile(row);
                row.Start += CellW;
            }

            if (row.Tiles.Count == 0)
                row.Start = Math.Max(row.Start, travelled);

            while (_entries.Count > 0 && row.Start - travelled + row.Tiles.Count * CellW < RowWidth)
                AppendTile(row, _entries[row.Next++ % _entries.Count]);

            row.Shift.X = row.Start - travelled;
        }

        private void AppendTile(Row row, ThumbnailEntry entry)
        {
            var brush = new ImageBrush
            {
                Source = entry.StaticSource,
                Stretch = Stretch.UniformToFill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };

            var tile = new Tile
            {
                Entry = entry,
                Border = new Border
                {
                    Width = ThumbW,
                    Height = ThumbH,
                    Margin = new Thickness(0, 0, GapX, 0),
                    CornerRadius = new CornerRadius(8),
                    Background = brush,
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x44, 0x44, 0x44)),
                    BorderThickness = new Thickness(2),
                },
            };

            if (entry.IsGif)
            {
                var master = AcquireMaster(entry);
                if (master.Source is IImageBrushSource current)
                    brush.Source = current;

                tile.Master = master;
                tile.FrameHandler = (_, e) =>
                {
                    if (e.Property == Image.SourceProperty && master.Source is IImageBrushSource frame)
                        brush.Source = frame;
                };
                master.PropertyChanged += tile.FrameHandler;
            }

            row.Tiles.Add(tile);
            row.Panel.Children.Add(tile.Border);
        }

        private void RemoveFirstTile(Row row)
        {
            var tile = row.Tiles[0];
            row.Tiles.RemoveAt(0);
            row.Panel.Children.RemoveAt(0);
            Unhook(tile);

            if (tile.Master != null)
                ReleaseMaster(tile.Entry);
        }

        private static void Unhook(Tile tile)
        {
            if (tile.Master != null && tile.FrameHandler != null)
                tile.Master.PropertyChanged -= tile.FrameHandler;
        }

        private Image AcquireMaster(ThumbnailEntry entry)
        {
            if (_gifMasters.TryGetValue(entry, out var existing))
            {
                _gifMasters[entry] = (existing.Master, existing.Uses + 1);
                return existing.Master;
            }

            var master = new Image
            {
                Width = ThumbW,
                Height = ThumbH,
                Stretch = Stretch.UniformToFill,
                Source = entry.StaticSource,
            };

            AnimationBehavior.SetCacheFramesInMemory(master, false);
            AnimationBehavior.SetSourceUri(master, new Uri(entry.TempFilePath, UriKind.Absolute));
            Canvas.SetLeft(master, 0);
            Canvas.SetTop(master, 0);
            GifMasterContainer.Children.Add(master);
            _gifMasters[entry] = (master, 1);
            return master;
        }

        private void ReleaseMaster(ThumbnailEntry entry)
        {
            if (!_gifMasters.TryGetValue(entry, out var existing))
                return;

            _gifMasters[entry] = (existing.Master, existing.Uses - 1);
            ReleaseUnusedMasters();
        }

        private void ReleaseUnusedMasters()
        {
            var unused = _gifMasters
                .Where(pair => pair.Value.Uses <= 0 && !_entries.Contains(pair.Key))
                .ToList();

            foreach (var (entry, (master, _)) in unused)
            {
                AnimationBehavior.SetSourceUri(master, null);
                GifMasterContainer.Children.Remove(master);
                _gifMasters.Remove(entry);
            }
        }

        private static int[] BuildShuffledOffsets(int numRows, int n)
        {
            var offsets = new int[numRows];

            for (int i = 0; i < numRows; i++)
                offsets[i] = i % n;

            var rng = new Random(42);
            for (int i = numRows - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int tmp = offsets[i];
                offsets[i] = offsets[j];
                offsets[j] = tmp;
            }

            return offsets;
        }
    }
}
