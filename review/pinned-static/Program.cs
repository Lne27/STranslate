using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Views;
using DrawingRectangle = System.Drawing.Rectangle;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/STranslate;component/Controls/ImageZoom.xaml")
        });
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                if (args.Contains("--components")) await ComponentMemory.Run();
                else await Run();
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { app.Shutdown(); }
        });
        app.Run();
    }

    private static async Task Run()
    {
        var controller = new PinnedWindowController(new Settings(), null!, null!);
        // 仅测静态窗口，不启动真实服务或获取桌面内容。窗口放在屏幕外，不干扰用户。
        var template = Snapshot();
        var snapshotClock = Stopwatch.StartNew();
        var snapshotAllocation = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            _ = PinnedImageTranslateSnapshot.Create(template.SourceImage, template.AnnotatedImage,
                template.TranslationOverlay, template.OriginalWords, template.TranslatedWords, template.PhysicalBounds);
        snapshotAllocation = GC.GetAllocatedBytesForCurrentThread() - snapshotAllocation;
        snapshotClock.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new { snapshotMeanMicroseconds = snapshotClock.Elapsed.TotalMicroseconds / 1000,
            snapshotAllocatedBytes = snapshotAllocation / 1000, originalCharacters = template.OriginalWords.Count,
            translatedCharacters = template.TranslatedWords.Count }));
        controller.CreateWindow(template).Close();
        await Settle();
        await Task.Delay(3000); // 等待首次 WPF/JIT/渲染初始化结束再测空闲。
        var baselineCpu = await IdleCpu();
        foreach (var count in new[] { 1, 10, 30 })
        {
            var snapshots = Enumerable.Range(0, count).Select(_ => Snapshot()).ToArray();
            await Settle();
            var before = Memory();
            var timer = Stopwatch.StartNew();
            var windows = snapshots.Select(controller.CreateWindow).ToArray();
            var createMs = timer.Elapsed.TotalMilliseconds;
            await Settle();
            timer.Stop();
            var after = Memory();
            await Task.Delay(3000);
            var cpu = await IdleCpu();
            var captureTimes = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                var clock = Stopwatch.StartNew();
                var lease = await controller.BeginCaptureAsync() ?? throw new Exception("Missing capture lease");
                var hidden = Application.Current.Windows.Cast<Window>().All(w => Cloaked(w) != 0);
                if (!hidden) throw new Exception("Content or chrome failed to cloak");
                if (await controller.BeginCaptureAsync() != null) throw new Exception("Duplicate capture was queued");
                await lease.DisposeAsync();
                await lease.DisposeAsync(); // 幂等释放，不重复释放 gate。
                if (Application.Current.Windows.Cast<Window>().Any(w => Cloaked(w) != 0))
                    throw new Exception("Content or chrome failed to restore");
                captureTimes.Add(clock.Elapsed.TotalMilliseconds);
            }
            var extraLease = await controller.BeginCaptureAsync() ?? throw new Exception("Missing lease");
            var duringCapture = controller.CreateWindow(template);
            if (Cloaked(duringCapture) == 0) throw new Exception("New pin was not cloaked");
            windows[0].Close();
            await extraLease.DisposeAsync();
            if (Cloaked(duringCapture) != 0) throw new Exception("New pin was not restored");
            duringCapture.Close();
            var weak = windows.Select(w => new WeakReference(w)).ToArray();
            controller.CloseAll();
            windows = null!;
            snapshots = null!;
            await Settle();
            var live = weak.Count(w => w.IsAlive);
            var closed = Memory();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                count, createMs, createAndSettleMs = timer.Elapsed.TotalMilliseconds,
                extraPrivateMiB = after.PrivateMiB - before.PrivateMiB,
                extraManagedMiB = after.ManagedMiB - before.ManagedMiB,
                baselineIdleCpuMsPer3Seconds = baselineCpu, idleCpuMsPer3Seconds = cpu,
                cloakAndRestoreMedianMs = captureTimes.Order().ElementAt(2),
                liveClosedWindows = live, closedPrivateMiB = closed.PrivateMiB,
                input = "800x400, two frozen BGRA images, one paragraph; off-screen windows"
            }));
            if (live != 0) throw new Exception("Closed pinned windows are retained");
        }
    }

    private static PinnedImageTranslateSnapshot Snapshot()
    {
        var source = BitmapSource.Create(800, 400, 96, 96, PixelFormats.Bgra32, null, new byte[800 * 400 * 4], 3200);
        source.Freeze();
        var annotated = source.Clone();
        annotated.Freeze();
        var block = new OcrLayoutBlock
        {
            Text = "A static pinned result retains its paragraph across wrapped visual lines.",
            BoxPoints = [new(20, 20), new(780, 20), new(780, 120), new(20, 120)],
            LineBoxPoints = [[new(20, 20), new(780, 20), new(780, 120), new(20, 120)]]
        };
        var overlay = ImageTranslateRenderer.CreateTranslatedOverlay([block], ImageTranslateOverlayTheme.Light);
        return PinnedImageTranslateSnapshot.Create(source, annotated, overlay, overlay.SelectableWords,
            overlay.SelectableWords, new DrawingRectangle(-20000, -20000, 800, 400));
    }

    private static async Task Settle()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static async Task<double> IdleCpu()
    {
        using var process = Process.GetCurrentProcess();
        var before = process.TotalProcessorTime;
        await Task.Delay(3000);
        process.Refresh();
        return (process.TotalProcessorTime - before).TotalMilliseconds;
    }

    private static (double PrivateMiB, double ManagedMiB) Memory()
    {
        using var process = Process.GetCurrentProcess();
        return (process.PrivateMemorySize64 / 1048576d, GC.GetTotalMemory(false) / 1048576d);
    }

    private static int Cloaked(Window window)
    {
        Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(new WindowInteropHelper(window).Handle, 14, out var value, sizeof(int)));
        return value;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
}
