using Grpc.Core;
using MareSynchronosServices.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Protos;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace MareSynchronosServices.Services;

internal class AuthenticationService : AuthService.AuthServiceBase
{
    private readonly ILogger<AuthenticationService> _logger;
    private readonly MareDbContext _dbContext;
    private readonly SecretKeyAuthenticationHandler _authHandler;

    public AuthenticationService(ILogger<AuthenticationService> logger, MareDbContext dbContext, SecretKeyAuthenticationHandler authHandler)
    {
        _logger = logger;
        _dbContext = dbContext;
        _authHandler = authHandler;
    }

    public override async Task Authorize(IAsyncStreamReader<AuthRequest> requestStream, IServerStreamWriter<AuthReply> responseStream, ServerCallContext context)
    {
        await foreach (var input in requestStream.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
        {
            var response = await _authHandler.AuthenticateAsync(_dbContext, input.Ip, input.SecretKey).ConfigureAwait(false);
            await responseStream.WriteAsync(response, context.CancellationToken).ConfigureAwait(false);
        }
    }

    public override Task<Empty> RemoveAuth(UidMessage request, ServerCallContext context)
    {
        _logger.LogInformation("Removing Authentication for {uid}", request.Uid);
        _authHandler.RemoveAuthentication(request.Uid);
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> ClearUnauthorized(Empty request, ServerCallContext context)
    {
        _logger.LogInformation("Clearing unauthorized users");
        _authHandler.ClearUnauthorizedUsers();
        return Task.FromResult(new Empty());
    }
}