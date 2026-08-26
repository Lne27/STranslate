using STranslate.Helpers;
using STranslate.Views;
using System.Windows;

namespace STranslate.Core;

/// <summary>
/// 管理 Pinned 窗口集合与截图期间的 Cloak 事务。
/// </summary>
public sealed class PinnedWindowController
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly HashSet<PinnedImageTranslateWindow> _windows = [];
    private readonly HashSet<PinnedImageTranslateWindow> _captureCloakedWindows = [];
    private readonly object _syncRoot = new();
    private bool _captureActive;

    internal PinnedImageTranslateWindow CreateWindow(PinnedImageTranslateSource source)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
            return Application.Current.Dispatcher.Invoke(() => CreateWindow(source));

        var window = new PinnedImageTranslateWindow(this);
        Register(window);
        try
        {
            window.Initialize(source);
            window.Show();
            window.Activate();
            return window;
        }
        catch
        {
            Unregister(window);
            try
            {
                window.Close();
            }
            catch
            {
                // 保留原始创建异常。
            }
            throw;
        }
    }

    internal void Register(PinnedImageTranslateWindow window)
    {
        lock (_syncRoot)
            _windows.Add(window);
    }

    internal void Unregister(PinnedImageTranslateWindow window)
    {
        lock (_syncRoot)
            _windows.Remove(window);
    }

    internal void OnWindowSourceInitialized(PinnedImageTranslateWindow window)
    {
        bool shouldCloak;
        lock (_syncRoot)
        {
            shouldCloak = _captureActive && _windows.Contains(window);
            if (shouldCloak)
                _captureCloakedWindows.Add(window);
        }

        if (!shouldCloak)
            return;

        window.CloseTransientUiForCapture();
        if (!window.SetCaptureCloaked(cloaked: true))
            throw new InvalidOperationException("Failed to cloak a pinned image-translate window created during capture.");
    }

    /// <summary>
    /// 截图期间用 DWM Cloak 隐藏所有 Pinned 窗口。
    /// </summary>
    internal async ValueTask<IAsyncDisposable> BeginCaptureAsync(CancellationToken cancellationToken = default)
    {
        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                List<PinnedImageTranslateWindow> snapshot;
                lock (_syncRoot)
                {
                    _captureActive = true;
                    _captureCloakedWindows.Clear();
                    snapshot = _windows.Where(window => window.IsVisible).ToList();
                    foreach (var window in snapshot)
                        _captureCloakedWindows.Add(window);
                }

                foreach (var window in snapshot)
                {
                    window.CloseTransientUiForCapture();
                    if (!window.SetCaptureCloaked(cloaked: true))
                        throw new InvalidOperationException("Failed to cloak a pinned image-translate window before capture.");
                }

                Win32Helper.FlushDesktopComposition();
            });

            return new CaptureLease(this);
        }
        catch
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                PinnedImageTranslateWindow[] rollbackWindows;
                lock (_syncRoot)
                    rollbackWindows = _captureCloakedWindows.ToArray();

                foreach (var window in rollbackWindows)
                {
                    bool stillRegistered;
                    lock (_syncRoot)
                        stillRegistered = _windows.Contains(window);

                    if (stillRegistered)
                    {
                        window.SetCaptureCloaked(cloaked: false);
                        window.RestoreTransientUiAfterCapture();
                    }
                }

                lock (_syncRoot)
                {
                    _captureActive = false;
                    _captureCloakedWindows.Clear();
                }

                Win32Helper.FlushDesktopComposition();
            });
            _captureGate.Release();
            throw;
        }
    }

    internal void CloseAll()
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(CloseAll);
            return;
        }

        PinnedImageTranslateWindow[] windows;
        lock (_syncRoot)
            windows = _windows.ToArray();

        foreach (var window in windows)
            window.Close();
    }

    private async ValueTask EndCaptureAsync()
    {
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                PinnedImageTranslateWindow[] windowsToUncloak;
                lock (_syncRoot)
                    windowsToUncloak = _captureCloakedWindows.ToArray();

                foreach (var window in windowsToUncloak)
                {
                    bool stillRegistered;
                    lock (_syncRoot)
                        stillRegistered = _windows.Contains(window);

                    if (stillRegistered)
                    {
                        window.SetCaptureCloaked(cloaked: false);
                        window.RestoreTransientUiAfterCapture();
                    }
                }

                lock (_syncRoot)
                {
                    _captureActive = false;
                    _captureCloakedWindows.Clear();
                }

                Win32Helper.FlushDesktopComposition();
            });
        }
        finally
        {
            _captureGate.Release();
        }
    }

    private sealed class CaptureLease(PinnedWindowController owner) : IAsyncDisposable
    {
        private PinnedWindowController? _owner = owner;

        public async ValueTask DisposeAsync()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner != null)
                await currentOwner.EndCaptureAsync();
        }
    }
}
