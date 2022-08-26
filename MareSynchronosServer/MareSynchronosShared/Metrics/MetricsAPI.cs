namespace MareSynchronosShared.Metrics;

public class MetricsAPI
{
    public const string CounterInitializedConnections = "mare_initialized_connections";
    public const string GaugeConnections = "mare_unauthorized_connections";
    public const string GaugeAuthorizedConnections = "mare_authorized_connections";
    public const string GaugeAvailableWorkerThreads = "mare_available_threadpool";
    public const string GaugeAvailableIOWorkerThreads = "mare_available_threadpool_io";
    public const string GaugeUsersRegistered = "mare_users_registered";
    public const string CounterUsersRegisteredDeleted = "mare_users_registered_deleted";
    public const string GaugePairs = "mare_pairs";
    public const string GaugePairsPaused = "mare_pairs_paused";
    public const string GaugeFilesTotal = "mare_files";
    public const string GaugeFilesTotalSize = "mare_files_size";
    public const string CounterUserPushData = "mare_user_push";
    public const string CounterUserPushDataTo = "mare_user_push_to";
    public const string CounterAuthenticationRequests = "mare_auth_requests";
    public const string CounterAuthenticationCacheHits = "mare_auth_requests_cachehit";
    public const string CounterAuthenticationFailures = "mare_auth_requests_fail";
    public const string CounterAuthenticationSuccesses = "mare_auth_requests_success";
}