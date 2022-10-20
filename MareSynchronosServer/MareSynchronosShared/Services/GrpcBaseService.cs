using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MareSynchronosShared.Services;

public abstract class GrpcBaseService : IHostedService, IDisposable
{
    protected GrpcBaseService(ILogger logger)
    {
        _logger = logger;
    }

    private CancellationTokenSource _faultCheckCts = new();
    private CancellationTokenSource _streamCts = new();
    private readonly ILogger _logger;
    protected bool GrpcIsFaulty { get; private set; }

    protected abstract Task StartAsyncInternal(CancellationToken cancellationToken);
    protected abstract Task StopAsyncInternal(CancellationToken cancellationToken);
    protected abstract Task OnGrpcRestore();
    protected abstract Task PreStartStream();
    protected abstract Task StartStream(CancellationToken ct);
    protected abstract Task PostStartStream();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = RestartStreams();
        _ = CheckGrpcFaults(_faultCheckCts.Token);
        await StartAsyncInternal(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _faultCheckCts.Cancel();
        _streamCts.Cancel();
        await StopAsyncInternal(cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartStreams()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = new();
        if (!GrpcIsFaulty)
        {
            try
            {
                await PreStartStream().ConfigureAwait(false);
                await StartStream(_streamCts.Token).ConfigureAwait(false);
                await PostStartStream().ConfigureAwait(false);
            }
            catch
            {
                SetGrpcFaulty();
            }
        }
    }

    protected void SetGrpcFaulty()
    {
        if (!GrpcIsFaulty)
        {
            GrpcIsFaulty = true;
            _logger.LogWarning("GRPC connection is faulty");
        }
    }

    private async Task CheckGrpcFaults(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckFaultStateAndRestore().ConfigureAwait(false);
            }
            catch { SetGrpcFaulty(); }
            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private async Task CheckFaultStateAndRestore()
    {
        if (GrpcIsFaulty)
        {
            await RestartStreams().ConfigureAwait(false);
            await OnGrpcRestore().ConfigureAwait(false);
            _logger.LogInformation("GRPC connection is restored");
            GrpcIsFaulty = false;
        }
    }

    protected async Task<T> InvokeOnGrpc<T>(AsyncUnaryCall<T> toExecute)
    {
        try
        {
            var result = await toExecute.ConfigureAwait(false);

            return result;
        }
        catch
        {
            SetGrpcFaulty();

            return default;
        }
    }

    protected async Task ExecuteOnGrpc<T>(AsyncUnaryCall<T> toExecute)
    {
        try
        {
            await toExecute.ConfigureAwait(false);
            await CheckFaultStateAndRestore().ConfigureAwait(false);
        }
        catch
        {
            SetGrpcFaulty();
        }
    }

    public void Dispose()
    {
        _streamCts?.Dispose();
        _faultCheckCts?.Dispose();
    }
}