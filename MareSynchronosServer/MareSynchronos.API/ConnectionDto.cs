namespace MareSynchronos.API
{
    public record ConnectionDto
    {
        public int ServerVersion { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsModerator { get; set; }
        public string UID { get; set; }
    }
}
