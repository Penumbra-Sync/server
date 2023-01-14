using MareSynchronosShared.Services;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace MareSynchronosShared.Utils;

public class ServerTokenGenerator
{
    private readonly IConfigurationService<MareConfigurationAuthBase> _configuration;
    private readonly ILogger<ServerTokenGenerator> _logger;

    private Dictionary<string, string> _tokenDictionary { get; set; } = new(StringComparer.Ordinal);
    public string Token
    {
        get
        {
            var currentJwt = _configuration.GetValue<string>(nameof(MareConfigurationAuthBase.Jwt));
            if (_tokenDictionary.TryGetValue(currentJwt, out var token))
            {
                return token;
            }

            return GenerateToken();
        }
    }

    public ServerTokenGenerator(IConfigurationService<MareConfigurationAuthBase> configuration, ILogger<ServerTokenGenerator> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GenerateToken()
    {
        var signingKey = _configuration.GetValue<string>(nameof(MareConfigurationAuthBase.Jwt));
        var authSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(signingKey));

        var token = new SecurityTokenDescriptor()
        {
            Subject = new ClaimsIdentity(new List<Claim>()
            {
                new Claim(MareClaimTypes.Uid, _configuration.GetValue<string>(nameof(MareConfigurationBase.ShardName))),
                new Claim(MareClaimTypes.Internal, "true"),
            }),
            SigningCredentials = new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256Signature),
        };

        var handler = new JwtSecurityTokenHandler();
        var rawData = handler.CreateJwtSecurityToken(token).RawData;

        _tokenDictionary[signingKey] = rawData;

        _logger.LogInformation("Generated Token: {data}", rawData);

        return rawData;
    }
}
