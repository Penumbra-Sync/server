using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Utils;

public class RequestFileStreamResult : FileStreamResult
{
    private readonly Action _onComplete;
    private readonly CancellationTokenSource _releaseCts = new();
    private bool _releasedSlot = false;

    public RequestFileStreamResult(Action onComplete, int secondsUntilRelease, Stream fileStream, string contentType) : base(fileStream, contentType)
    {
        _onComplete = onComplete;
        // forcefully release slot
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(secondsUntilRelease), _releaseCts.Token)
                .ContinueWith(c =>
                {
                    if (!c.IsCanceled)
                    {
                        _releasedSlot = true;
                        _onComplete.Invoke();
                    }
                }).ConfigureAwait(false);
        });
    }

    public override void ExecuteResult(ActionContext context)
    {
        base.ExecuteResult(context);

        _releaseCts.Cancel();

        if (!_releasedSlot)
            _onComplete.Invoke();
    }

    public override async Task ExecuteResultAsync(ActionContext context)
    {
        await base.ExecuteResultAsync(context).ConfigureAwait(false);

        _releaseCts.Cancel();

        if (!_releasedSlot)
            _onComplete.Invoke();
    }
}