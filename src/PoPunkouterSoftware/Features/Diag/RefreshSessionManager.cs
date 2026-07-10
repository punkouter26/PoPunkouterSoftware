namespace PoPunkouterSoftware.Features.Diag;

internal sealed class RefreshSessionManager
{
    public readonly SemaphoreSlim Lock = new(1, 1);
    private volatile CancellationTokenSource? _activeCts;

    public void SetActiveCts(CancellationTokenSource? cts) => _activeCts = cts;
    public void Cancel() => _activeCts?.Cancel();
}
