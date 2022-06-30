namespace MareSynchronos.API
{
    public record LoggedInUserDto
    {
        public bool IsAdmin { get; set; }
        public bool IsModerator { get; set; }
        public string UID { get; set; }
    }
}
