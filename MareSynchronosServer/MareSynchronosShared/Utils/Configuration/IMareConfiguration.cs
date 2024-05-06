namespace MareSynchronosShared.Utils.Configuration;

public interface IMareConfiguration
{
    T GetValueOrDefault<T>(string key, T defaultValue);
    T GetValue<T>(string key);
    string SerializeValue(string key, string defaultValue);
}
