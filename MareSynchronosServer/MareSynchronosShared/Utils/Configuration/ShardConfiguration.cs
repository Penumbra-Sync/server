namespace MareSynchronosShared.Utils.Configuration;

public class ShardConfiguration
{
    public List<string> Continents { get; set; }
    public string FileMatch { get; set; }
    public Dictionary<string, Uri> RegionUris { get; set; }
}