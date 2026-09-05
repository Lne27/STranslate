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
            zoom.SelectTextAtPoint(new Point(25, 25), selectVisualLine: false);
            Assert.Equal("hello", zoom.SelectedText);
            Assert.True(zoom.IsPointOverTextSelection(new Point(25, 25)));
            Assert.False(zoom.IsPointOverTextSelection(new Point(95, 25)));
            zoom.SelectTextAtPoint(new Point(25, 25), selectVisualLine: true);
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
            zoom.SelectTextAtPoint(new Point(25, 25), selectVisualLine: false);
            Assert.Equal("hello", zoom.SelectedText);
            zoom.SelectTextAtPoint(new Point(125, 25), selectVisualLine: true);
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

    private static ObservableCollection<OcrWord> Words(string text) => OcrWordBuilder.CreateIndexedCollection(
        text.Select((ch, index) => new OcrWord { Text = ch.ToString(), BoundingBox = new Rect(10 + index * 10, 20, 10, 20) }), true);

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
