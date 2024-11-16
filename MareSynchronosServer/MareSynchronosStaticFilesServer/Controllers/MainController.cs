using MareSynchronos.API.Routes;
using MareSynchronosShared.Utils.Configuration;
using MareSynchronosStaticFilesServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MareSynchronosStaticFilesServer.Controllers;

[Route(MareFiles.Main)]
[Authorize(Policy = "Internal")]
public class MainController : ControllerBase
{
    private readonly IClientReadyMessageService _messageService;
    private readonly MainServerShardRegistrationService _shardRegistrationService;

    public MainController(ILogger<MainController> logger, IClientReadyMessageService mareHub,
        MainServerShardRegistrationService shardRegistrationService) : base(logger)
    {
        _messageService = mareHub;
        _shardRegistrationService = shardRegistrationService;
    }

    [HttpGet(MareFiles.Main_SendReady)]
    public async Task<IActionResult> SendReadyToClients(string uid, Guid requestId)
    {
        await _messageService.SendDownloadReady(uid, requestId).ConfigureAwait(false);
        return Ok();
    }

    [HttpPost("shardRegister")]
    public IActionResult RegisterShard([FromBody] ShardConfiguration shardConfiguration)
    {
        try
        {
            _shardRegistrationService.RegisterShard(MareUser, shardConfiguration);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shard could not be registered {shard}", MareUser);
            return BadRequest();
        }
    }

    [HttpPost("shardUnregister")]
    public IActionResult UnregisterShard()
    {
        _shardRegistrationService.UnregisterShard(MareUser);
        return Ok();
    }

    [HttpPost("shardHeartbeat")]
    public IActionResult ShardHeartbeat()
    {
        try
        {
            _shardRegistrationService.ShardHeartbeat(MareUser);
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shard not registered: {shard}", MareUser);
            return BadRequest();
        }
    }
}