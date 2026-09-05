using STranslate.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Plugin;
using STranslate.Views;
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DrawingRectangle = System.Drawing.Rectangle;

namespace STranslate.Tests;

public class PinnedImageTranslateTests
{
    [Theory]
    [InlineData("\"pinned\"")]
    [InlineData("\"Pinned\"")]
    [InlineData("2")]
    public void LegacyPinnedSettingMigratesWithoutLosingOtherSettings(string mode)
    {
        var settings = JsonSerializer.Deserialize<Settings>(
            $$"""{"ImageTranslateWindowMode":{{mode}},"FontSize":19,"Language":"ja","PinnedImageTranslateShowShadow":false} """)!;
        Assert.Equal(ImageTranslateWindowMode.Compact, settings.ImageTranslateWindowMode);
        Assert.Equal(19, settings.FontSize);
        Assert.Equal("ja", settings.Language);
        Assert.False(settings.PinnedImageTranslateShowShadow);
        Assert.Contains("\"ImageTranslateWindowMode\":\"compact\"", JsonSerializer.Serialize(settings));
        Assert.Equal([ImageTranslateWindowMode.Standalone, ImageTranslateWindowMode.Compact], Enum.GetValues<ImageTranslateWindowMode>());
    }

    [Theory]
    [InlineData(ImageTranslateWindowMode.Standalone, true)]
    [InlineData(ImageTranslateWindowMode.Compact, false)]
    public void CapturePaddingKeepsCompactPhysicalSize(ImageTranslateWindowMode mode, bool expected) =>
        Assert.Equal(expected, Screenshot.ShouldPadImage(mode));

    [Theory]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(320)]
    public void SnapshotKeepsExactImageSizeAndOwnsSelectionData(int size)
    {
        RunOnSta(() =>
        {
            var image = Image(size, size);
            var overlay = Overlay();
            var words = Words("hello world");
            var bounds = new DrawingRectangle(-200, 120, size, size);
            var snapshot = PinnedImageTranslateSnapshot.Create(image, image, overlay, words, overlay.SelectableWords, bounds);
            var originalText = string.Concat(snapshot.OriginalWords.Select(x => x.Text));
            var translatedText = string.Concat(snapshot.TranslatedWords.Select(x => x.Text));
            Assert.Same(image, snapshot.SourceImage);
            Assert.Same(image, snapshot.AnnotatedImage);
            Assert.Equal(bounds, snapshot.PhysicalBounds);
            Assert.NotSame(words[0], snapshot.OriginalWords[0]);
            Assert.NotSame(overlay.SelectableWords[0], snapshot.TranslatedWords[0]);
            words[0].Text = "changed";
            words.Clear();
            overlay.SelectableWords[0].Text = "changed";
            Assert.Equal(originalText, string.Concat(snapshot.OriginalWords.Select(x => x.Text)));
            Assert.Equal(translatedText, string.Concat(snapshot.TranslatedWords.Select(x => x.Text)));
            Assert.Equal("hello world", snapshot.TranslationOverlay.Items[0].Text);
        });
    }

    [Fact]
    public void SnapshotRejectsIncompleteOrRescaledResults()
    {
        RunOnSta(() =>
        {
            var image = Image(32, 32);
            var mutable = new WriteableBitmap(32, 32, 96, 96, PixelFormats.Bgra32, null);
            var bounds = new DrawingRectangle(10, 20, 32, 32);
            Assert.Throws<ArgumentException>(() => PinnedImageTranslateSnapshot.Create(image, image,
                ImageTranslateOverlayDocument.Empty, [], [], bounds));
            Assert.Throws<ArgumentException>(() => PinnedImageTranslateSnapshot.Create(image, image,
                Overlay(), [], [], new DrawingRectangle(10, 20, 64, 64)));
            Assert.Throws<ArgumentException>(() => PinnedImageTranslateSnapshot.Create(image, Image(64, 64),
                Overlay(), [], [], bounds));
            Assert.Throws<ArgumentException>(() => PinnedImageTranslateSnapshot.Create(mutable, image,
                Overlay(), [], [], bounds));
            Assert.Throws<ArgumentException>(() => PinnedImageTranslateSnapshot.Create(image, image,
                Overlay(), [], [], DrawingRectangle.Empty));
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2)]
    public void ChromeRetainsOriginalPhysicalMarginAtEveryDpi(double scale)
    {
        var image = new DrawingRectangle(-320, 240, 127, 83);
        var outer = PinnedImageTranslateChromeWindow.CalculateOuterBounds(image, new DpiScale(scale, scale));
        var margin = (int)Math.Ceiling(10 * scale);
        Assert.Equal(DrawingRectangle.FromLTRB(image.Left - margin, image.Top - margin,
            image.Right + margin, image.Bottom + margin), outer);
    }

    [Fact]
    public void ChromeRetainsOriginalActiveGlowAndInactiveShadowContract()
    {
        RunOnSta(() =>
        {
            var chrome = new PinnedImageTranslateChromeWindow();
            try
            {
                var grid = Assert.IsType<Grid>(chrome.Content);
                var shadow = Assert.IsType<Border>(grid.Children[0]);
                var glow = Assert.IsType<Border>(grid.Children[1]);
                AssertCaster(shadow, Colors.White, Colors.Black, 8, 0.36, RenderingBias.Performance);
                AssertCaster(glow, Color.FromRgb(0x4D, 0x90, 0xFE), Color.FromRgb(0x4D, 0x90, 0xFE), 6, 0.42, RenderingBias.Quality);
                foreach (var shadowEnabled in new[] { false, true })
                {
                    chrome.UpdateVisual(true, shadowEnabled);
                    Assert.Equal(Visibility.Visible, glow.Visibility);
                    Assert.Equal(Visibility.Collapsed, shadow.Visibility);
                    chrome.UpdateVisual(false, shadowEnabled);
                    Assert.Equal(Visibility.Collapsed, glow.Visibility);
                    Assert.Equal(shadowEnabled ? Visibility.Visible : Visibility.Collapsed, shadow.Visibility);
                }
            }
            finally { chrome.Close(); }
        });
    }

    [Theory]
    [InlineData("hello world", 1, "hello")]
    [InlineData("hello world", 8, "world")]
    [InlineData("foo_bar", 4, "foo_bar")]
    [InlineData("foo-bar", 1, "foo")]
    [InlineData("foo-bar", 3, "-")]
    [InlineData("don't", 1, "don")]
    [InlineData("abc123", 4, "abc123")]
    [InlineData("中文文字测试", 2, "中文文字测试")]
    [InlineData("(hello)", 0, "(")]
    [InlineData("hello\r\nworld", 8, "world")]
    [InlineData("a 😀 b", 3, "😀")]
    [InlineData("a 👨‍👩‍👦 b", 5, "👨‍👩‍👦")]
    [InlineData("café next", 4, "café")]
    public void WordSelectionPreservesUnicodeTextElements(string text, int index, string expected)
    {
        Assert.True(OcrWordSelection.TryGetWordRange(text, index, out var start, out var end));
        Assert.Equal(expected, text[start..(end + 1)]);
    }

    [Fact]
    public void SelectionSwitchingLayersClearsOldHighlightAndCopyAllDoesNotSelect()
    {
        RunOnSta(() =>
        {
            var zoom = CreateImageZoom();
            zoom.OcrWords = Words("hello world");
            zoom.Measure(new Size(320, 120));
            zoom.Arrange(new Rect(0, 0, 320, 120));
            zoom.UpdateLayout();
            Assert.Equal("hello world", zoom.GetFullText());
            Assert.Empty(zoom.SelectedText);
            zoom.SelectTextAtPoint(new Point(25, 25), selectParagraph: false);
            Assert.Equal("hello", zoom.SelectedText);
            Assert.True(zoom.IsPointOverTextSelection(new Point(25, 25)));
            Assert.False(zoom.IsPointOverTextSelection(new Point(95, 25)));
            zoom.SelectTextAtPoint(new Point(25, 25), selectParagraph: true);
            Assert.Equal("hello world", zoom.SelectedText);
            zoom.OcrWords = Words("原文");
            Assert.Empty(zoom.SelectedText);
            Assert.Equal("原文", zoom.GetFullText());
            zoom.SelectAllText();
            Assert.Equal("原文", zoom.SelectedText);
            zoom.OcrWords = [];
            Assert.Empty(zoom.SelectedText);
            Assert.Empty(zoom.GetFullText());
        });
    }

    [Fact]
    public void WordSelectionDoesNotJoinIndependentOverlayBlocks()
    {
        RunOnSta(() =>
        {
            var first = Words("hello");
            var second = Words("world");
            foreach (var word in second)
                word.BoundingBox = new Rect(word.BoundingBox.Left + 100, 20, 10, 20);
            var zoom = CreateImageZoom();
            zoom.OcrWords = OcrWordBuilder.CreateIndexedCollectionFromGroups([first, second]);
            zoom.Measure(new Size(320, 120));
            zoom.Arrange(new Rect(0, 0, 320, 120));
            zoom.UpdateLayout();
            zoom.SelectTextAtPoint(new Point(25, 25), selectParagraph: false);
            Assert.Equal("hello", zoom.SelectedText);
            zoom.SelectTextAtPoint(new Point(125, 25), selectParagraph: true);
            Assert.Equal("world", zoom.SelectedText);
        });
    }

    private static ImageZoom CreateImageZoom()
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/STranslate;component/Controls/ImageZoom.xaml")
        };
        var zoom = new ImageZoom
        {
            Width = 320, Height = 120, Source = Image(320, 120),
            Style = (Style)dictionary[typeof(ImageZoom)], DisableAnimation = true, IsPanAndZoomEnabled = false
        };
        zoom.ApplyTemplate();
        return zoom;
    }

    [Theory]
    [InlineData(LayoutAnalysisMode.Auto)]
    [InlineData(LayoutAnalysisMode.Provider)]
    [InlineData(LayoutAnalysisMode.Smart)]
    [InlineData(LayoutAnalysisMode.NoMerge)]
    public void SourceParagraphSelectionUsesAnalyzedMembershipAndPreservesCoordinates(LayoutAnalysisMode mode)
    {
        RunOnSta(() =>
        {
            var lines = new[]
            {
                SourceLine("This is the first line", 10, 10, 180),
                SourceLine("continued on the next line", 10, 34, 210),
                SourceLine("A separate paragraph", 10, 100, 190)
            };
            var ocr = new OcrResult
            {
                OcrContents = lines.ToList(),
                Regions = [new() { Paragraphs = [new() { Lines = [lines[0], lines[1]] }, new() { Lines = [lines[2]] }] }]
            };
            var blocks = OcrLayoutAnalyzer.AnalyzeBlocks(ocr, mode);
            var words = OcrWordBuilder.CreateFromLayoutBlocks(blocks);
            var anchor = words.First(w => w.BoundingBox.Top == 34);
            Assert.True(OcrWordSelection.TryGetParagraphRange(words, anchor, out var start, out var end));
            var selected = string.Concat(words.Select(w => w.Text))[start..(end + 1)];
            Assert.Equal(mode == LayoutAnalysisMode.NoMerge ? lines[1].Text :
                lines[0].Text + Environment.NewLine + lines[1].Text, selected);
            Assert.DoesNotContain(lines[2].Text, selected);
            Assert.Equal(10, anchor.BoundingBox.Left);
            Assert.Equal(210d / lines[1].Text.Length, anchor.BoundingBox.Width);
            var image = Image(320, 120);
            var snapshot = PinnedImageTranslateSnapshot.Create(image, image, Overlay(), words, words,
                new DrawingRectangle(0, 0, 320, 120), showOriginal: true);
            Assert.True(snapshot.ShowOriginal);
            Assert.Equal(anchor.ParagraphIndex, snapshot.OriginalWords.First(w => w.BoundingBox.Top == 34).ParagraphIndex);
        });
    }

    [Fact]
    public void TripleClickSelectsAllWrappedLinesWithoutCrossingAdjacentParagraph()
    {
        RunOnSta(() =>
        {
            const string first = "This is a paragraph with enough text to wrap across several visual lines.";
            var formatted = ImageTranslateRenderer.CreateFormattedText(first, 16, Brushes.Black, 180,
                double.PositiveInfinity, 20, false, 1);
            var firstWords = OcrWordBuilder.CreateFromFormattedText(first, formatted, new Point(10, 10),
                new Rect(10, 10, 180, 110), 1);
            var other = Words("Another paragraph");
            foreach (var word in other)
                word.BoundingBox = new Rect(word.BoundingBox.Left + 190, 20, 5, 20);
            var zoom = CreateImageZoom();
            zoom.OcrWords = OcrWordBuilder.CreateIndexedCollectionFromGroups([firstWords, other], separateParagraphs: true);
            zoom.Measure(new Size(320, 120));
            zoom.Arrange(new Rect(0, 0, 320, 120));
            zoom.UpdateLayout();
            var firstParagraph = zoom.OcrWords.Where(w => w.ParagraphIndex == 0 && !w.BoundingBox.IsEmpty).ToArray();
            Assert.True(firstParagraph.Select(w => w.VisualLineIndex).Distinct().Count() > 1);
            foreach (var line in firstParagraph.GroupBy(w => w.VisualLineIndex))
            {
                var bounds = line.First().BoundingBox;
                zoom.SelectTextAtPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2),
                    selectParagraph: true);
                Assert.Equal(first, zoom.SelectedText);
            }
            Assert.Equal(first + Environment.NewLine + "Another paragraph", zoom.GetFullText());
        });
    }

    [Fact]
    public void ParagraphSelectionKeepsInterleavedColumnsSeparate()
    {
        var blocks = OcrLayoutAnalyzer.AnalyzeBlocks(new[]
        {
            SourceLine("Left column starts here", 0, 0, 180),
            SourceLine("Right column starts here", 300, 0, 190),
            SourceLine("and continues below", 0, 24, 160),
            SourceLine("with its own text", 300, 24, 150)
        }, LayoutAnalysisMode.Smart);
        var words = OcrWordBuilder.CreateFromLayoutBlocks(blocks);
        var anchor = words.First(w => w.BoundingBox.Left == 0 && w.BoundingBox.Top == 24);
        Assert.True(OcrWordSelection.TryGetParagraphRange(words, anchor, out var start, out var end));
        Assert.Equal("Left column starts here" + Environment.NewLine + "and continues below",
            string.Concat(words.Select(w => w.Text))[start..(end + 1)]);
    }

    private static OcrContent SourceLine(string text, float x, float y, float width) => new()
    {
        Text = text, BoxPoints = [new(x, y), new(x + width, y), new(x + width, y + 20), new(x, y + 20)]
    };

    [Fact]
    public void ClippedTranslationKeepsCompleteParagraphForCopying()
    {
        RunOnSta(() =>
        {
            const string text = "This complete paragraph must remain available even when its tail is clipped.";
            var formatted = ImageTranslateRenderer.CreateFormattedText(text, 16, Brushes.Black, 800,
                100, 0, true, 1, maxLineCount: 1);
            var words = OcrWordBuilder.CreateIndexedCollectionFromGroups([
                OcrWordBuilder.CreateFromFormattedText(text, formatted, new Point(10, 10), new Rect(10, 10, 70, 40), 1,
                    preserveClippedText: true)
            ]);
            Assert.Contains(words, w => w.BoundingBox.IsEmpty && !string.IsNullOrWhiteSpace(w.Text));
            var zoom = CreateImageZoom();
            zoom.OcrWords = words;
            zoom.Measure(new Size(320, 120));
            zoom.Arrange(new Rect(0, 0, 320, 120));
            zoom.UpdateLayout();
            var anchor = words.First(w => !w.BoundingBox.IsEmpty).BoundingBox;
            zoom.SelectTextAtPoint(new Point(anchor.Left + anchor.Width / 2, anchor.Top + anchor.Height / 2),
                selectParagraph: true);
            Assert.Equal(text, zoom.SelectedText);
            Assert.Equal(text, zoom.GetFullText());
        });
    }

    [Theory]
    [InlineData(-2000, -1000, 300, 200, 0, 0)]
    [InlineData(3000, 2000, 300, 200, 1620, 880)]
    [InlineData(-2000, -1000, 3000, 2000, 0, 0)]
    public void RemovedMonitorRecoveryKeepsPhysicalSize(int x, int y, int width, int height, int expectedX, int expectedY)
    {
        Assert.Equal(new DrawingRectangle(expectedX, expectedY, width, height),
            PinnedImageTranslateWindow.ClampToWorkArea(new DrawingRectangle(x, y, width, height), new Rect(0, 0, 1920, 1080)));
    }

    private static ObservableCollection<OcrWord> Words(string text) => OcrWordBuilder.CreateIndexedCollection(
        text.Select((ch, index) => new OcrWord { Text = ch.ToString(), ParagraphIndex = 0, BoundingBox = new Rect(10 + index * 10, 20, 10, 20) }), true);

    private static BitmapSource Image(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze();
        return image;
    }

    private static ImageTranslateOverlayDocument Overlay()
    {
        List<BoxPoint> box = [new(10, 20), new(200, 20), new(200, 60), new(10, 60)];
        return ImageTranslateRenderer.CreateTranslatedOverlay(
            [new OcrLayoutBlock { Text = "hello world", BoxPoints = box, LineBoxPoints = [box] }], ImageTranslateOverlayTheme.Light);
    }

    private static void AssertCaster(Border caster, Color background, Color color, double blur, double opacity, RenderingBias bias)
    {
        Assert.Equal(new Thickness(10), caster.Margin);
        Assert.Equal(background, Assert.IsType<SolidColorBrush>(caster.Background).Color);
        var effect = Assert.IsType<DropShadowEffect>(caster.Effect);
        Assert.Equal(color, effect.Color);
        Assert.Equal(blur, effect.BlurRadius);
        Assert.Equal(opacity, effect.Opacity);
        Assert.Equal(bias, effect.RenderingBias);
        Assert.Equal(0, effect.ShadowDepth);
        Assert.Equal(0, effect.Direction);
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
