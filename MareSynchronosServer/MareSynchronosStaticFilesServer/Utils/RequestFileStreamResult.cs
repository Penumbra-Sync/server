using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Utils;

public class RequestFileStreamResult : FileStreamResult
{
    private readonly Action _onComplete;

    public RequestFileStreamResult(Action onComplete, Stream fileStream, string contentType) : base(fileStream, contentType)
    {
        _onComplete = onComplete;
    }

    public override void ExecuteResult(ActionContext context)
    {
        base.ExecuteResult(context);

        _onComplete.Invoke();
    }

    public override async Task ExecuteResultAsync(ActionContext context)
    {
        await base.ExecuteResultAsync(context).ConfigureAwait(false);

        _onComplete.Invoke();
    }
}