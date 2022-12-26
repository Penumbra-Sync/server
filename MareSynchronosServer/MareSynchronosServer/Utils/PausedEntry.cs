namespace MareSynchronosServer.Utils;

public record PausedEntry
{
    public string UID { get; set; }
    public List<PauseState> PauseStates { get; set; } = new();

    public PauseInfo IsDirectlyPaused => PauseStateWithoutGroups == null ? PauseInfo.NoConnection
        : PauseStates.First(g => g.GID == null).IsPaused ? PauseInfo.Paused : PauseInfo.Unpaused;

    public PauseInfo IsPausedPerGroup => !PauseStatesWithoutDirect.Any() ? PauseInfo.NoConnection
        : PauseStatesWithoutDirect.All(p => p.IsPaused) ? PauseInfo.Paused : PauseInfo.Unpaused;

    private IEnumerable<PauseState> PauseStatesWithoutDirect => PauseStates.Where(f => f.GID != null);
    private PauseState PauseStateWithoutGroups => PauseStates.SingleOrDefault(p => p.GID == null);

    public bool IsPaused
    {
        get
        {
            var isDirectlyPaused = IsDirectlyPaused;
            bool result;
            if (isDirectlyPaused != PauseInfo.NoConnection)
            {
                result = isDirectlyPaused == PauseInfo.Paused;
            }
            else
            {
                result = IsPausedPerGroup == PauseInfo.Paused;
            }

            return result;
        }
    }

    public PauseInfo IsOtherPausedForSpecificGroup(string gid)
    {
        var state = PauseStatesWithoutDirect.SingleOrDefault(g => string.Equals(g.GID, gid, StringComparison.Ordinal));
        if (state == null) return PauseInfo.NoConnection;
        return state.IsOtherPaused ? PauseInfo.Paused : PauseInfo.Unpaused;
    }

    public PauseInfo IsPausedForSpecificGroup(string gid)
    {
        var state = PauseStatesWithoutDirect.SingleOrDefault(g => string.Equals(g.GID, gid, StringComparison.Ordinal));
        if (state == null) return PauseInfo.NoConnection;
        return state.IsPaused ? PauseInfo.Paused : PauseInfo.NoConnection;
    }

    public PauseInfo IsPausedExcludingGroup(string gid)
    {
        var states = PauseStatesWithoutDirect.Where(f => !string.Equals(f.GID, gid, StringComparison.Ordinal)).ToList();
        if (!states.Any()) return PauseInfo.NoConnection;
        var result = states.All(p => p.IsPaused);
        if (result) return PauseInfo.Paused;
        return PauseInfo.Unpaused;
    }
}
