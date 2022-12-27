using Grpc.Net.ClientFactory;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using static MareSynchronosShared.Protos.ConfigurationService;

namespace MareSynchronosShared.Services;

public class MareConfigurationServiceClient<T> : IConfigurationService<T> where T : class, IMareConfiguration
{
    internal record RemoteCachedEntry(object Value, DateTime Inserted);

    private readonly T _config;
    private readonly ConcurrentDictionary<string, RemoteCachedEntry> _cachedRemoteProperties = new(StringComparer.Ordinal);
    private readonly ILogger<MareConfigurationServiceClient<T>> _logger;
    private readonly ConfigurationServiceClient _configurationServiceClient;

    public MareConfigurationServiceClient(ILogger<MareConfigurationServiceClient<T>> logger, IOptions<T> config, ConfigurationServiceClient configurationServiceClient)
    {
        _config = config.Value;
        _logger = logger;
        _configurationServiceClient = configurationServiceClient;
    }

    public MareConfigurationServiceClient(ILogger<MareConfigurationServiceClient<T>> logger, IOptions<T> config, GrpcClientFactory grpcClientFactory, string grpcClientName)
    {
        _config = config.Value;
        _logger = logger;
        _configurationServiceClient = grpcClientFactory.CreateClient<ConfigurationServiceClient>(grpcClientName);
    }

    public bool IsMain => false;

    public T1 GetValueOrDefault<T1>(string key, T1 defaultValue)
    {
        var prop = _config.GetType().GetProperty(key);
        if (prop == null) return defaultValue;
        if (prop.PropertyType != typeof(T1)) throw new InvalidCastException($"Invalid Cast: Property {key} is {prop.PropertyType}, wanted: {typeof(T1)}");
        bool isRemote = prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), inherit: true).Any();
        if (isRemote)
        {
            bool isCurrent = false;
            if (_cachedRemoteProperties.TryGetValue(key, out var existingEntry) && existingEntry.Inserted > DateTime.Now - TimeSpan.FromMinutes(30))
            {
                isCurrent = true;
            }

            if (!isCurrent)
            {
                var result = GetValueFromGrpc(key, defaultValue, prop.PropertyType);
                if (result == null) return defaultValue;
                _cachedRemoteProperties[key] = result;
                return (T1)_cachedRemoteProperties[key].Value;
            }
        }

        var value = prop.GetValue(_config);
        return (T1)value;
    }

    private RemoteCachedEntry? GetValueFromGrpc(string key, object defaultValue, Type t)
    {
        // grab stuff from grpc
        try
        {
            _logger.LogInformation("Getting {key} from Grpc", key);
            var response = _configurationServiceClient.GetConfigurationEntry(new KeyMessage { Key = key, Default = Convert.ToString(defaultValue, CultureInfo.InvariantCulture) });
            _logger.LogInformation("Grpc Response for {key} = {value}", key, response.Value);
            return new RemoteCachedEntry(JsonSerializer.Deserialize(response.Value, t), DateTime.Now);
        }
        catch
        {
            return null;
        }
    }

    public T1 GetValue<T1>(string key)
    {
        var prop = _config.GetType().GetProperty(key);
        if (prop == null) throw new KeyNotFoundException(key);
        if (prop.PropertyType != typeof(T1)) throw new InvalidCastException($"Invalid Cast: Property {key} is {prop.PropertyType}, wanted: {typeof(T1)}");
        bool isRemote = prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), inherit: true).Any();
        if (isRemote)
        {
            bool isCurrent = false;
            if (_cachedRemoteProperties.TryGetValue(key, out var existingEntry) && existingEntry.Inserted > DateTime.Now - TimeSpan.FromMinutes(30))
            {
                isCurrent = true;
            }

            if (!isCurrent)
            {
                var result = GetValueFromGrpc(key, null, prop.PropertyType);
                if (result == null) throw new KeyNotFoundException(key);
                _cachedRemoteProperties[key] = result;
            }

            if (!_cachedRemoteProperties.ContainsKey(key)) throw new KeyNotFoundException(key);

            return (T1)_cachedRemoteProperties[key].Value;
        }

        var value = prop.GetValue(_config);
        return (T1)value;
    }

    public override string ToString()
    {
        var props = _config.GetType().GetProperties();
        StringBuilder sb = new();
        foreach (var prop in props)
        {
            var isRemote = prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), true).Any();
            var mi = GetType().GetMethod(nameof(GetValue)).MakeGenericMethod(prop.PropertyType);
            var val = mi.Invoke(this, new[] { prop.Name });
            var value = isRemote ? val : prop.GetValue(_config);
            sb.AppendLine($"{prop.Name} (IsRemote: {isRemote}) => {value}");
        }
        return sb.ToString();
    }
}