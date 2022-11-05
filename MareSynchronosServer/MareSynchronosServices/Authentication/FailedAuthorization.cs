using System;
using System.Threading;
using System.Threading.Tasks;

namespace MareSynchronosServices.Authentication;

internal class FailedAuthorization
{
    private int failedAttempts = 1;
    public int FailedAttempts => failedAttempts;
    public Task ResetTask { get; set; }
    public void IncreaseFailedAttempts()
    {
        Interlocked.Increment(ref failedAttempts);
    }
}