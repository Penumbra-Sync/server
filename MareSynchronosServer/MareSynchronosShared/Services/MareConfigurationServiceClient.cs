using Grpc.Net.ClientFactory;
using MareSynchronosShared.Protos;
using MareSynchronosShared.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using static MareSynchronosShared.Protos.ConfigurationService;

namespace MareSynchronosShared.Services;

public class MareConfigurationServiceClient<T> : IHostedService, IConfigurationService<T> where T : class, IMareConfiguration
{
    internal record RemoteCachedEntry(object Value, DateTime Inserted);

    private readonly T _config;
    private readonly ConcurrentDictionary<string, object> _cachedRemoteProperties = new(StringComparer.Ordinal);
    private readonly ILogger<MareConfigurationServiceClient<T>> _logger;
    private readonly GrpcClientFactory _grpcClientFactory;
    private readonly string _grpcClientName;
    private readonly CancellationTokenSource _updateTaskCts = new();
    private ConfigurationServiceClient _configurationServiceClient;
    private bool _initialized = false;

    public MareConfigurationServiceClient(ILogger<MareConfigurationServiceClient<T>> logger, IOptions<T> config, GrpcClientFactory grpcClientFactory, string grpcClientName)
    {
        _config = config.Value;
        _logger = logger;
        _grpcClientFactory = grpcClientFactory;
        _grpcClientName = grpcClientName;
    }

    public bool IsMain => false;

    public T1 GetValueOrDefault<T1>(string key, T1 defaultValue)
    {
        var prop = _config.GetType().GetProperty(key);
        if (prop == null) return defaultValue;
        if (prop.PropertyType != typeof(T1)) throw new InvalidCastException($"Invalid Cast: Property {key} is {prop.PropertyType}, wanted: {typeof(T1)}");
        bool isRemote = prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), inherit: true).Any();
        if (isRemote && _cachedRemoteProperties.TryGetValue(key, out var remotevalue))
        {
            return (T1)remotevalue;
        }

        var value = prop.GetValue(_config);
        var defaultPropValue = prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null;
        if (value == defaultPropValue) return defaultValue;
        return (T1)value;
    }

    public T1 GetValue<T1>(string key)
    {
        var prop = _config.GetType().GetProperty(key);
        if (prop == null) throw new KeyNotFoundException(key);
        if (prop.PropertyType != typeof(T1)) throw new InvalidCastException($"Invalid Cast: Property {key} is {prop.PropertyType}, wanted: {typeof(T1)}");
        bool isRemote = prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), inherit: true).Any();
        if (isRemote && _cachedRemoteProperties.TryGetValue(key, out var remotevalue))
        {
            return (T1)remotevalue;
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

    private async Task<T1> GetValueFromGrpc<T1>(ConfigurationServiceClient client, string key, object defaultValue)
    {
        // grab stuff from grpc
        try
        {
            _logger.LogInformation("Getting {key} from Grpc", key);
            var response = await client.GetConfigurationEntryAsync(new KeyMessage { Key = key, Default = Convert.ToString(defaultValue, CultureInfo.InvariantCulture) }).ConfigureAwait(false);
            _logger.LogInformation("Grpc Response for {key} = {value}", key, response.Value);
            return JsonSerializer.Deserialize<T1>(response.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failure Getting Remote Entry for {key}", key);
            return (T1)defaultValue;
        }
    }

    private async Task UpdateRemoteProperties(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Getting Properties from GRPC");
            try
            {
                _configurationServiceClient = _grpcClientFactory.CreateClient<ConfigurationServiceClient>(_grpcClientName);
                var properties = _config.GetType().GetProperties();
                foreach (var prop in properties)
                {
                    _logger.LogInformation("Checking Property " + prop.Name);
                    try
                    {
                        if (!prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), true).Any()) continue;
                        var mi = GetType().GetMethod(nameof(GetValueFromGrpc), BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(prop.PropertyType);
                        var defaultValue = prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null;
                        var task = (Task)mi.Invoke(this, new[] { _configurationServiceClient, prop.Name, defaultValue });
                        await task.ConfigureAwait(false);

                        var resultProperty = task.GetType().GetProperty("Result");
                        var resultValue = resultProperty.GetValue(task);

                        if (resultValue != defaultValue)
                        {
                            _cachedRemoteProperties[prop.Name] = resultValue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during getting property " + prop.Name);
                    }
                }

                if (!_initialized)
                {
                    _initialized = true;
                    _logger.LogInformation("Finished initial getting properties from GRPC");
                    _logger.LogInformation(ToString());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failure getting or updating properties from GRPC, retrying in 30min");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), ct).ConfigureAwait(false);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting MareConfigurationServiceClient");
        _ = UpdateRemoteProperties(_updateTaskCts.Token);
        while (!_initialized && !cancellationToken.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _updateTaskCts.Cancel();
        return Task.CompletedTask;
    }
}