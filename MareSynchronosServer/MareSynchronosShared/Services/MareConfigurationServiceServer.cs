using MareSynchronosShared.Utils;
using Microsoft.Extensions.Options;
using System.Text;

namespace MareSynchronosShared.Services;

public class MareConfigurationServiceServer<T> : IConfigurationService<T> where T : class, IMareConfiguration
{
    private readonly T _config;
    public bool IsMain => true;

    public MareConfigurationServiceServer(IOptions<T> config)
    {
        _config = config.Value;
    }

    public T1 GetValueOrDefault<T1>(string key, T1 defaultValue)
    {
        return _config.GetValueOrDefault<T1>(key, defaultValue);
    }

    public T1 GetValue<T1>(string key)
    {
        return _config.GetValue<T1>(key);
    }

    public override string ToString()
    {
        var props = _config.GetType().GetProperties();
        StringBuilder sb = new();
        foreach (var prop in props)
        {
            sb.AppendLine($"{prop.Name} (IsRemote: {prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), true).Any()}) => {prop.GetValue(_config)}");
        }

        sb.AppendLine(_config.ToString());

        return sb.ToString();
    }
}
