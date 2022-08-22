namespace MareSynchronosServices.Authentication;

public class FailedAuthorization : IDisposable
{
    private int failedAttempts = 1;
    public int FailedAttempts => failedAttempts;
    public Task ResetTask { get; set; }
    public CancellationTokenSource? ResetCts { get; set; }

    public void Dispose()
    {
        try
        {
            ResetCts?.Cancel();
            ResetCts?.Dispose();
        }
        catch { }
    }

    public void IncreaseFailedAttempts()
    {
        Interlocked.Increment(ref failedAttempts);
    }
}