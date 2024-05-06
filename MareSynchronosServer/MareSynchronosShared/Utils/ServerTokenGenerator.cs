using MareSynchronosShared.Utils.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace MareSynchronosShared.Utils;

public class ServerTokenGenerator
{
    private readonly IOptionsMonitor<MareConfigurationBase> _configuration;
    private readonly ILogger<ServerTokenGenerator> _logger;

    private Dictionary<string, string> _tokenDictionary { get; set; } = new(StringComparer.Ordinal);
    public string Token
    {
        get
        {
            var currentJwt = _configuration.CurrentValue.Jwt;
            if (_tokenDictionary.TryGetValue(currentJwt, out var token))
            {
                return token;
            }

            return GenerateToken();
        }
    }

    public ServerTokenGenerator(IOptionsMonitor<MareConfigurationBase> configuration, ILogger<ServerTokenGenerator> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private string GenerateToken()
    {
        var signingKey = _configuration.CurrentValue.Jwt;
        var authSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(signingKey));

        var token = new SecurityTokenDescriptor()
        {
            Subject = new ClaimsIdentity(new List<Claim>()
            {
                new Claim(MareClaimTypes.Uid, _configuration.CurrentValue.ShardName),
                new Claim(MareClaimTypes.Internal, "true"),
                new Claim(MareClaimTypes.Expires, DateTime.Now.AddYears(1).Ticks.ToString(CultureInfo.InvariantCulture))
            }),
            SigningCredentials = new SigningCredentials(authSigningKey, SecurityAlgorithms.HmacSha256Signature),
            Expires = DateTime.Now.AddYears(1)
        };

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.CreateJwtSecurityToken(token);
        var rawData = jwt.RawData;

        _tokenDictionary[signingKey] = rawData;

        _logger.LogInformation("Generated Token: {data}", rawData);

        return rawData;
    }
}
