using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MareSynchronosShared.Services;

public class MareConfigurationServiceClient<T> : IHostedService, IConfigurationService<T> where T : class, IMareConfiguration
{
    private readonly IOptionsMonitor<T> _config;
    private readonly ConcurrentDictionary<string, object> _cachedRemoteProperties = new(StringComparer.Ordinal);
    private readonly ILogger<MareConfigurationServiceClient<T>> _logger;
    private readonly ServerTokenGenerator _serverTokenGenerator;
    private readonly CancellationTokenSource _updateTaskCts = new();
    private bool _initialized = false;
    private readonly HttpClient _httpClient;

    private Uri GetRoute(string key, string value)
    {
        if (_config.CurrentValue.GetType() == typeof(ServerConfiguration))
            return new Uri((_config.CurrentValue as ServerConfiguration).MainServerAddress, $"configuration/MareServerConfiguration/{nameof(MareServerConfigurationController.GetConfigurationEntry)}?key={key}&defaultValue={value}");
        if (_config.CurrentValue.GetType() == typeof(MareConfigurationBase))
            return new Uri((_config.CurrentValue as MareConfigurationBase).MainServerAddress, $"configuration/MareBaseConfiguration/{nameof(MareBaseConfigurationController.GetConfigurationEntry)}?key={key}&defaultValue={value}");
        if (_config.CurrentValue.GetType() == typeof(ServicesConfiguration))
            return new Uri((_config.CurrentValue as ServicesConfiguration).MainServerAddress, $"configuration/MareServicesConfiguration/{nameof(MareServicesConfigurationController.GetConfigurationEntry)}?key={key}&defaultValue={value}");
        if (_config.CurrentValue.GetType() == typeof(StaticFilesServerConfiguration))
            return new Uri((_config.CurrentValue as StaticFilesServerConfiguration).MainFileServerAddress, $"configuration/MareStaticFilesServerConfiguration/{nameof(MareStaticFilesServerConfigurationController.GetConfigurationEntry)}?key={key}&defaultValue={value}");

        throw new NotSupportedException("Config is not supported to be gotten remotely");
    }

    public MareConfigurationServiceClient(ILogger<MareConfigurationServiceClient<T>> logger, IOptionsMonitor<T> config, ServerTokenGenerator serverTokenGenerator)
    {
        _config = config;
        _logger = logger;
        _serverTokenGenerator = serverTokenGenerator;
        _httpClient = new();
    }

    public bool IsMain => false;

    public T1 GetValueOrDefault<T1>(string key, T1 defaultValue)
    {
        var prop = _config.CurrentValue.GetType().GetProperty(key);
        if (prop == null) return defaultValue;
        if (prop.PropertyType != typeof(T1)) throw new InvalidCastException($"Invalid Cast: Property {key} is {prop.PropertyType}, wanted: {typeof(T1)}");
        bool isRemote = prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), inherit: true).Any();
        if (isRemote && _cachedRemoteProperties.TryGetValue(key, out var remotevalue))
        {
            return (T1)remotevalue;
        }

        var value = prop.GetValue(_config.CurrentValue);
        var defaultPropValue = prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null;
        if (value == defaultPropValue) return defaultValue;
        return (T1)value;
    }

    public T1 GetValue<T1>(string key)
    {
        var prop = _config.CurrentValue.GetType().GetProperty(key);
        if (prop == null) throw new KeyNotFoundException(key);
        if (prop.PropertyType != typeof(T1)) throw new InvalidCastException($"Invalid Cast: Property {key} is {prop.PropertyType}, wanted: {typeof(T1)}");
        bool isRemote = prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), inherit: true).Any();
        if (isRemote && _cachedRemoteProperties.TryGetValue(key, out var remotevalue))
        {
            return (T1)remotevalue;
        }

        var value = prop.GetValue(_config.CurrentValue);
        return (T1)value;
    }

    public override string ToString()
    {
        var props = _config.CurrentValue.GetType().GetProperties();
        StringBuilder sb = new();
        foreach (var prop in props)
        {
            var isRemote = prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), true).Any();
            var getValueMethod = GetType().GetMethod(nameof(GetValue)).MakeGenericMethod(prop.PropertyType);
            var value = isRemote ? getValueMethod.Invoke(this, new[] { prop.Name }) : prop.GetValue(_config.CurrentValue);
            if (typeof(IEnumerable).IsAssignableFrom(prop.PropertyType) && !typeof(string).IsAssignableFrom(prop.PropertyType))
            {
                var enumVal = (IEnumerable)value;
                value = string.Empty;
                foreach (var listVal in enumVal)
                {
                    value += listVal.ToString() + ", ";
                }
            }
            sb.AppendLine($"{prop.Name} (IsRemote: {isRemote}) => {value}");
        }
        return sb.ToString();
    }

    private async Task<T1> GetValueFromRemote<T1>(string key, object defaultValue)
    {
        try
        {
            _logger.LogInformation("Getting {key} from Http", key);
            using HttpRequestMessage msg = new(HttpMethod.Get, GetRoute(key, Convert.ToString(defaultValue, CultureInfo.InvariantCulture)));
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serverTokenGenerator.Token);
            using var response = await _httpClient.SendAsync(msg).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            _logger.LogInformation("Http Response for {key} = {value}", key, content);
            return JsonSerializer.Deserialize<T1>(content);
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
            _logger.LogInformation("Getting Properties from Remote for " + typeof(T));
            try
            {
                var properties = _config.CurrentValue.GetType().GetProperties();
                foreach (var prop in properties)
                {
                    try
                    {
                        if (!prop.GetCustomAttributes(typeof(RemoteConfigurationAttribute), true).Any()) continue;
                        _logger.LogInformation("Checking Property " + prop.Name);
                        var mi = GetType().GetMethod(nameof(GetValueFromRemote), BindingFlags.NonPublic | BindingFlags.Instance).MakeGenericMethod(prop.PropertyType);
                        var defaultValue = prop.PropertyType.IsValueType ? Activator.CreateInstance(prop.PropertyType) : null;
                        var task = (Task)mi.Invoke(this, new[] { prop.Name, defaultValue });
                        await task.ConfigureAwait(false);

                        var resultProperty = task.GetType().GetProperty("Result");
                        var resultValue = resultProperty.GetValue(task);

                        if (resultValue != defaultValue)
                        {
                            _cachedRemoteProperties[prop.Name] = resultValue;
                            _logger.LogInformation(prop.Name + " is now " + resultValue.ToString());
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
                }

                _logger.LogInformation("Saved properties from HTTP are now:");
                _logger.LogInformation(ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failure getting or updating properties from HTTP, retrying in 30min");
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
        _httpClient.Dispose();
        return Task.CompletedTask;
    }
}