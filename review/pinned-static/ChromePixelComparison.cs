using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using STranslate.Views;

internal static class ChromePixelComparison
{
    internal static async Task Preview()
    {
        var canvas = new Canvas { Background = Brushes.LightGray };
        var holders = new List<PinnedImageTranslateChromeWindow>();
        for (var row = 0; row < 2; row++)
        {
            var old = ChromeReference.Create(row == 1);
            old.Width = 340; old.Height = 200;
            Canvas.SetLeft(old, 20); Canvas.SetTop(old, 20 + row * 220);
            canvas.Children.Add(old);
            var chrome = new PinnedImageTranslateChromeWindow();
            chrome.UpdateVisual(row == 1, true);
            var current = (FrameworkElement)chrome.Content;
            chrome.Content = null;
            current.Width = 340; current.Height = 200;
            Canvas.SetLeft(current, 404); Canvas.SetTop(current, 20 + row * 220);
            canvas.Children.Add(current);
            holders.Add(chrome);
        }
        var window = new Window { Title = "STranslate Chrome pixel verification", Width = 768, Height = 460,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.LightGray, Content = canvas, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.SetResult();
        window.Show();
        await closed.Task;
        foreach (var holder in holders) holder.Close();
    }

    internal static void Run()
    {
        var cases = 0;
        foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2, 2.5, 3 })
        foreach (var size in new[] { new Size(32, 32), new Size(63, 47), new Size(800, 400), new Size(855, 529) })
        foreach (var active in new[] { false, true })
        {
            var grid = ChromeReference.Create(active);
            var optimized = new PinnedImageTranslateChromeWindow();
            optimized.UpdateVisual(active, true);
            var sliced = (FrameworkElement)optimized.Content;
            optimized.Content = null;
            var width = (int)size.Width + 2 * (int)Math.Ceiling(10 * scale);
            var height = (int)size.Height + 2 * (int)Math.Ceiling(10 * scale);
            var a = Render(grid, width, height, scale);
            var b = Render(sliced, width, height, scale);
            var different = a.Zip(b).Count(p => p.First != p.Second);
            Console.WriteLine(JsonSerializer.Serialize(new { scale, width, height, active, differentBytes = different,
                maxDifference = a.Zip(b).Max(p => Math.Abs(p.First - p.Second)) }));
            optimized.Close();
            if (different != 0) throw new InvalidOperationException("Chrome pixels changed.");
            cases++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { cases }));
    }

    private static byte[] Render(FrameworkElement element, int width, int height, double scale)
    {
        VisualTreeHelper.SetRootDpi(element, new DpiScale(scale, scale));
        element.Measure(new Size(width / scale, height / scale));
        element.Arrange(new Rect(0, 0, width / scale, height / scale));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
