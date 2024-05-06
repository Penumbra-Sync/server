using MareSynchronosShared.Utils.Configuration;

namespace MareSynchronosShared.Services;

public interface IConfigurationService<T> where T : class, IMareConfiguration
{
    bool IsMain { get; }
    T1 GetValue<T1>(string key);
    T1 GetValueOrDefault<T1>(string key, T1 defaultValue);
    string ToString();
}
