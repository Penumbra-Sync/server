namespace MareSynchronosServer.Utils;

public record PauseState
{
    public string GID { get; set; }
    public bool IsPaused => IsSelfPaused || IsOtherPaused;
    public bool IsSelfPaused { get; set; }
    public bool IsOtherPaused { get; set; }
}