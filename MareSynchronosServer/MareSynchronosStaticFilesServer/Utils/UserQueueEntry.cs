namespace MareSynchronosStaticFilesServer.Utils;

public class UserQueueEntry
{
    public UserQueueEntry(UserRequest userRequest, DateTime expirationDate)
    {
        UserRequest = userRequest;
        ExpirationDate = expirationDate;
    }

    public void MarkActive()
    {
        IsActive = true;
        ActivationDate = DateTime.UtcNow;
    }

    public UserRequest UserRequest { get; }
    public DateTime ExpirationDate { get; }
    public bool IsActive { get; private set; } = false;
    public DateTime ActivationDate { get; private set; }
}
