namespace MareSynchronos.API
{
    public record OnlineUserDto
    {
        public string UID { get; set; }
        public string CharacterNameHash { get; set; }
        public bool IsModerator { get; set; }
        public bool IsAdmin { get; set; }
    }
}
