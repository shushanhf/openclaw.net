namespace OpenClaw.Gateway.Composition;

internal sealed class AsyncStartupCleanupGuard : IAsyncDisposable
{
    private Func<ValueTask>? _cleanup;

    public void Register(Func<ValueTask> cleanup)
        => _cleanup = cleanup;

    public void Cancel()
        => _cleanup = null;

    public async ValueTask DisposeAsync()
    {
        if (_cleanup is null)
            return;

        await _cleanup();
        _cleanup = null;
    }
}
