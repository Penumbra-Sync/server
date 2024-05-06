namespace MareSynchronosAuthService.Authentication;

internal record SecretKeyFailedAuthorization
{
    private int failedAttempts = 1;
    public int FailedAttempts => failedAttempts;
    public Task ResetTask { get; set; }
    public void IncreaseFailedAttempts()
    {
        Interlocked.Increment(ref failedAttempts);
    }
}
