namespace MareSynchronosServer.Utils;

public record PauseState
{
    public string GID { get; set; }
    public bool IsPaused { get; set; }
}