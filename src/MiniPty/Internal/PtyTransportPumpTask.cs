namespace MiniPty.Internal;

/// <summary>
/// macOS-only transport pump scheduling via
/// <see cref="ThreadPool.UnsafeQueueUserWorkItem(System.Threading.IThreadPoolWorkItem, bool)"/>
/// with <c>preferLocal: true</c>. Callers on other platforms should keep using <see cref="Task.Run{TResult}(Func{TResult}, CancellationToken)"/>.
/// </summary>
internal static class PtyTransportPumpTask
{
    internal static Task<T> Run<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.UnsafeQueueUserWorkItem(
            new PumpWorkItem<T>(work, cancellationToken, tcs),
            preferLocal: true);
        return tcs.Task;
    }

    private sealed class PumpWorkItem<T> : IThreadPoolWorkItem
    {
        private readonly Func<CancellationToken, T> _work;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _tcs;

        internal PumpWorkItem(
            Func<CancellationToken, T> work,
            CancellationToken cancellationToken,
            TaskCompletionSource<T> tcs)
        {
            _work = work;
            _cancellationToken = cancellationToken;
            _tcs = tcs;
        }

        public void Execute()
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(_cancellationToken);
                return;
            }

            try
            {
                _tcs.SetResult(_work(_cancellationToken));
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == _cancellationToken)
            {
                _tcs.TrySetCanceled(_cancellationToken);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }
}
