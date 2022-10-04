namespace MareSynchronosServer.Hubs;

public partial class MareHub
{
    private record UserPair
    {
        public string UserUID { get; set; }
        public string OtherUserUID { get; set; }
        public bool UserPausedOther { get; set; }
        public bool OtherPausedUser { get; set; }
    }
}
