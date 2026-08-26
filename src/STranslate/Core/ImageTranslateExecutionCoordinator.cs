using STranslate.Plugin;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace STranslate.Core;

/// <summary>
/// 协调共享图片翻译服务的串行执行与安全释放。
/// </summary>
public sealed class ImageTranslateExecutionCoordinator
{
    private readonly ConcurrentDictionary<Service, ServiceExecutionState> _states =
        new(ReferenceEqualityComparer.Instance);
    // 防止旧 Service 引用在退役后重新进入执行队列。
    private readonly ConditionalWeakTable<Service, RetiredMarker> _retiredServices = new();

    internal async ValueTask<ServiceLease> AcquireAsync(Service service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (_retiredServices.TryGetValue(service, out _))
            throw new InvalidOperationException($"Service '{service.DisplayName}' is being removed.");

        var state = _states.GetOrAdd(service, static value => new ServiceExecutionState(value));
        await state.Gate.WaitAsync(cancellationToken);

        // 拿到 gate 后再次确认取消状态，避免过期请求进入插件。
        if (cancellationToken.IsCancellationRequested)
        {
            state.Gate.Release();
            cancellationToken.ThrowIfCancellationRequested();
        }

        lock (state.SyncRoot)
        {
            // 与 Retire 竞争时，在临界区内再次确认服务状态。
            if (state.IsRetiring || _retiredServices.TryGetValue(service, out _))
            {
                state.Gate.Release();
                throw new InvalidOperationException($"Service '{service.DisplayName}' is being removed.");
            }

            state.ActiveLeases++;
        }

        return new ServiceLease(this, state);
    }

    /// <summary>
    /// 退役服务，并延迟到最后一个 lease 释放后再 Dispose。
    /// </summary>
    internal void Retire(Service service, Action disposeAction)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(disposeAction);

        _retiredServices.GetValue(service, static _ => new RetiredMarker());

        var state = _states.GetOrAdd(service, static value => new ServiceExecutionState(value));
        Action? disposeNow = null;

        lock (state.SyncRoot)
        {
            state.IsRetiring = true;
            state.DisposeAction ??= disposeAction;
            if (state.ActiveLeases == 0)
            {
                disposeNow = state.DisposeAction;
                state.DisposeAction = null;
            }
        }

        if (disposeNow != null)
        {
            _states.TryRemove(service, out _);
            disposeNow();
        }
    }

    private void Release(ServiceExecutionState state)
    {
        Action? disposeNow = null;

        lock (state.SyncRoot)
        {
            if (state.ActiveLeases > 0)
                state.ActiveLeases--;

            if (state.IsRetiring && state.ActiveLeases == 0 && state.DisposeAction != null)
            {
                disposeNow = state.DisposeAction;
                state.DisposeAction = null;
            }
        }

        // 唤醒排队请求，让其看到退役状态并退出。
        state.Gate.Release();

        if (disposeNow != null)
        {
            _states.TryRemove(state.Service, out _);
            disposeNow();
        }
    }

    internal sealed class ServiceExecutionState(Service service)
    {
        internal Service Service { get; } = service;
        internal object SyncRoot { get; } = new();
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal int ActiveLeases { get; set; }
        internal bool IsRetiring { get; set; }
        internal Action? DisposeAction { get; set; }
    }

    private sealed class RetiredMarker;

    internal sealed class ServiceLease : IDisposable, IAsyncDisposable
    {
        private ImageTranslateExecutionCoordinator? _owner;
        private ServiceExecutionState? _state;

        internal ServiceLease(ImageTranslateExecutionCoordinator owner, ServiceExecutionState state)
        {
            _owner = owner;
            _state = state;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            var state = Interlocked.Exchange(ref _state, null);
            if (owner != null && state != null)
                owner.Release(state);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
