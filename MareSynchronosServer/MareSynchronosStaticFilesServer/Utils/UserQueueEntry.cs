namespace MareSynchronosStaticFilesServer.Utils;

public record UserQueueEntry(UserRequest UserRequest, DateTime ExpirationDate)
{
    public bool IsActive { get; set; } = false;
}
