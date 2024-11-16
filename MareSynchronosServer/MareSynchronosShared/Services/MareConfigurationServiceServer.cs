using MareSynchronosShared.Utils;
using MareSynchronosShared.Utils.Configuration;
using Microsoft.Extensions.Options;
using System.Collections;
using System.Text;

namespace MareSynchronosShared.Services;

public sealed class MareConfigurationServiceServer<T> : IDisposable, IConfigurationService<T> where T : class, IMareConfiguration
{
    private readonly IOptionsMonitor<T> _config;
    private bool _disposed;

    public bool IsMain => true;
    public event EventHandler ConfigChangedEvent;
    private IDisposable _onChanged;

    public MareConfigurationServiceServer(IOptionsMonitor<T> config)
    {
        _config = config;
        _onChanged = config.OnChange((c) => { ConfigChangedEvent?.Invoke(this, EventArgs.Empty); });
    }

    public T1 GetValueOrDefault<T1>(string key, T1 defaultValue)
    {
        return _config.CurrentValue.GetValueOrDefault<T1>(key, defaultValue);
    }

    public T1 GetValue<T1>(string key)
    {
        return _config.CurrentValue.GetValue<T1>(key);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _onChanged.Dispose();
        _disposed = true;
    }
}
