namespace MareSynchronos.API
{
    public class ClientPairDto
    {
        public string OtherUID { get; set; }
        public bool IsPaused { get; set; }
        public bool IsSynced { get; set; }
        public bool IsPausedFromOthers { get; set; }
        public bool IsRemoved { get; set; }
        public bool AllowReceiveMessages { get; set; }
    }
}