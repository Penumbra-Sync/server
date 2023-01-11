﻿using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MareSynchronosShared.Utils;

public class MareConfigurationBase : IMareConfiguration
{
    public int DbContextPoolSize { get; set; } = 100;
    public string ShardName { get; set; } = string.Empty;
    public int MetricsPort { get; set; }

    public T GetValue<T>(string key)
    {
        var prop = GetType().GetProperty(key);
        if (prop == null) throw new KeyNotFoundException(key);
        if (prop.PropertyType != typeof(T)) throw new ArgumentException($"Requested {key} with T:{typeof(T)}, where {key} is {prop.PropertyType}");
        return (T)prop.GetValue(this);
    }

    public T GetValueOrDefault<T>(string key, T defaultValue)
    {
        var prop = GetType().GetProperty(key);
        if (prop.PropertyType != typeof(T)) throw new ArgumentException($"Requested {key} with T:{typeof(T)}, where {key} is {prop.PropertyType}");
        if (prop == null) return defaultValue;
        return (T)prop.GetValue(this);
    }

    public string SerializeValue(string key, string defaultValue)
    {
        var prop = GetType().GetProperty(key);
        if (prop == null) return defaultValue;
        if (prop.GetCustomAttribute<RemoteConfigurationAttribute>() == null) return defaultValue;
        return JsonSerializer.Serialize(prop.GetValue(this), prop.PropertyType);
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(ShardName)} => {ShardName}");
        sb.AppendLine($"{nameof(DbContextPoolSize)} => {DbContextPoolSize}");
        return sb.ToString();
    }
}