using Grpc.Core;
using MareSynchronosServices.Authentication;
using MareSynchronosShared.Data;
using MareSynchronosShared.Protos;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace MareSynchronosServices.Services;

public class AuthenticationService : AuthService.AuthServiceBase
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

    public override async Task<AuthReply> Authorize(AuthRequest request, ServerCallContext context)
    {
        return await _authHandler.AuthenticateAsync(_dbContext, request.Ip, request.SecretKey);
    }

    public override Task<Empty> RemoveAuth(RemoveAuthRequest request, ServerCallContext context)
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