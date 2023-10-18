namespace MareSynchronosShared.Models;

public class UserPermissionQuery
{
    public string UserUID { get; set; }
    public string OtherUserUID { get; set; }
    public string Alias { get; set; }
    public string GID { get; set; }
    public bool Synced { get; set; }
    public bool? OwnpermIsPaused { get; set; }
    public bool? OwnpermSticky { get; set; }
    public bool? OwnpermDisableAnimations { get; set; }
    public bool? OwnpermDisableSounds { get; set; }
    public bool? OwnpermDisableVFX { get; set; }
    public bool? OtherpermIsPaused { get; set; }
    public bool? OtherpermDisableAnimations { get; set; }
    public bool? OtherpermDisableSounds { get; set; }
    public bool? OtherpermDisableVFX { get; set; }

    public UserPermissionSet? OwnPermissions => OwnpermSticky == null ? null : new UserPermissionSet
    {
        UserUID = UserUID,
        OtherUserUID = OtherUserUID,
        IsPaused = OwnpermIsPaused.Value,
        DisableAnimations = OwnpermDisableAnimations.Value,
        DisableSounds = OwnpermDisableSounds.Value,
        DisableVFX = OwnpermDisableVFX.Value,
        Sticky = OwnpermSticky.Value
    };

    public UserPermissionSet? OtherPermissions => !Synced ? null : new UserPermissionSet
    {
        UserUID = OtherUserUID,
        OtherUserUID = UserUID,
        IsPaused = OtherpermIsPaused ?? false,
        DisableAnimations = OtherpermDisableAnimations ?? false,
        DisableSounds = OtherpermDisableSounds ?? false,
        DisableVFX = OtherpermDisableVFX ?? false,
    };
}