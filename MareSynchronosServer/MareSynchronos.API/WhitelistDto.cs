namespace MareSynchronos.API
{
    public class WhitelistDto
    {
        public string OtherUID { get; set; }
        public bool IsPaused { get; set; }
        public bool IsSynced { get; set; }
        public bool IsPausedFromOthers { get; set; }
    }
}