using System.ComponentModel.DataAnnotations;

namespace MareSynchronosShared.Models;

public enum CharaDataAccess
{
    Individuals,
    ClosePairs,
    AllPairs,
    Public
}

public enum CharaDataShare
{
    Private,
    Shared
}

public class CharaData
{
    public string Id { get; set; }
    public virtual User Uploader { get; set; }
    public string UploaderUID { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime UpdatedDate { get; set; }
    public string Description { get; set; }
    public CharaDataAccess AccessType { get; set; }
    public CharaDataShare ShareType { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string? GlamourerData { get; set; }
    public string? CustomizeData { get; set; }
    public string? ManipulationData { get; set; }
    public int DownloadCount { get; set; } = 0;
    public virtual ICollection<CharaDataPose> Poses { get; set; } = [];
    public virtual ICollection<CharaDataFile> Files { get; set; } = [];
    public virtual ICollection<CharaDataFileSwap> FileSwaps { get; set; } = [];
    public virtual ICollection<CharaDataOriginalFile> OriginalFiles { get; set; } = [];
    public virtual ICollection<CharaDataAllowance> AllowedIndividiuals { get; set; } = [];
}

public class CharaDataAllowance
{
    [Key]
    public long Id { get; set; }
    public virtual CharaData Parent { get; set; }
    public string ParentId { get; set; }
    public string ParentUploaderUID { get; set; }
    public virtual User? AllowedUser { get; set; }
    public string? AllowedUserUID { get; set; }
    public virtual Group? AllowedGroup { get; set; }
    public string? AllowedGroupGID { get; set; }
}

public class CharaDataOriginalFile
{
    public virtual CharaData Parent { get; set; }
    public string ParentId { get; set; }
    public string ParentUploaderUID { get; set; }
    public string GamePath { get; set; }
    public string Hash { get; set; }
}

public class CharaDataFile
{
    public virtual FileCache FileCache { get; set; }
    public string FileCacheHash { get; set; }
    public string GamePath { get; set; }
    public virtual CharaData Parent { get; set; }
    public string ParentId { get; set; }
    public string ParentUploaderUID { get; set; }
}

public class CharaDataFileSwap
{
    public virtual CharaData Parent { get; set; }
    public string ParentId { get; set; }
    public string ParentUploaderUID { get; set; }
    public string GamePath { get; set; }
    public string FilePath { get; set; }
}

public class CharaDataPose
{
    public long Id { get; set; }
    public virtual CharaData Parent { get; set; }
    public string ParentId { get; set; }
    public string ParentUploaderUID { get; set; }
    public string Description { get; set; }
    public string PoseData { get; set; }
    public string WorldData { get; set; }
}