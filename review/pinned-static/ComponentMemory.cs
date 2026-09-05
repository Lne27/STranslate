using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Views;
using DrawingRectangle = System.Drawing.Rectangle;

internal static class ComponentMemory
{
    private const int Count = 30;

    internal static async Task Run()
    {
        // 先预热同一条组件装配路径，释放后开始记录。
        await Measure(1, false);
        await Task.Delay(3000);
        await Measure(Count, true);
    }

    private static async Task Measure(int count, bool report)
    {
        var controller = new PinnedWindowController(new Settings(), null!, null!);
        var samples = new List<object>();
        async Task Sample(string stage)
        {
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(500);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess();
            samples.Add(new { stage, privateBytes = process.PrivateMemorySize64,
                managedBytes = GC.GetTotalMemory(false) });
        }

        await Sample("baseline");
        var sources = Enumerable.Range(0, count).Select(_ => Image()).ToArray();
        await Sample("sourceImages");
        var annotated = Enumerable.Range(0, count).Select(_ => Image()).ToArray();
        await Sample("annotatedImages");
        var overlays = Enumerable.Range(0, count).Select(_ => Overlay()).ToArray();
        var snapshots = Enumerable.Range(0, count).Select(i => PinnedImageTranslateSnapshot.Create(
            sources[i], annotated[i], overlays[i], overlays[i].SelectableWords, overlays[i].SelectableWords,
            new DrawingRectangle(-20000, -20000, 800, 400))).ToArray();
        await Sample("textAndSnapshots");
        var windows = snapshots.Select(snapshot =>
        {
            var window = new PinnedImageTranslateWindow(controller) { ShowActivated = false };
            window.Initialize(snapshot, false);
            window.Show();
            return window;
        }).ToArray();
        await Sample("contentWindows");
        // 只在测试程序中取得实际伴随窗，分阶段显示；不修改生产代码。
        var field = typeof(PinnedImageTranslateWindow).GetField("_chromeWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var window in windows)
        {
            var chrome = (PinnedImageTranslateChromeWindow)field.GetValue(window)!;
            chrome.UpdateVisual(false, true);
            chrome.EnsureShownBehind(window);
        }
        await Sample("shadowWindows");
        // 桌面正常使用时只有一个贴图激活，其余显示阴影。
        var activeChrome = (PinnedImageTranslateChromeWindow)field.GetValue(windows[0])!;
        activeChrome.UpdateVisual(true, true);
        await Sample("oneActiveGlow");
        if (report) Console.WriteLine(JsonSerializer.Serialize(new
        {
            count, imageWidth = 800, imageHeight = 400, dpi = VisualTreeHelper.GetDpi(windows[0]).PixelsPerInchX,
            os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            renderingTier = RenderCapability.Tier >> 16, samples
        }));
        foreach (var window in windows) window.Close();
        GC.KeepAlive(sources); GC.KeepAlive(annotated); GC.KeepAlive(overlays); GC.KeepAlive(snapshots);
    }

    private static BitmapSource Image()
    {
        var result = BitmapSource.Create(800, 400, 96, 96, PixelFormats.Bgra32, null, new byte[800 * 400 * 4], 3200);
        result.Freeze();
        return result;
    }

    private static ImageTranslateOverlayDocument Overlay() => ImageTranslateRenderer.CreateTranslatedOverlay(
        [new OcrLayoutBlock
        {
            Text = "A static pinned result retains its paragraph across wrapped visual lines.",
            BoxPoints = [new(20, 20), new(780, 20), new(780, 120), new(20, 120)],
            LineBoxPoints = [[new(20, 20), new(780, 20), new(780, 120), new(20, 120)]]
        }], ImageTranslateOverlayTheme.Light);
}
