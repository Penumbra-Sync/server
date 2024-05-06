namespace MareSynchronosShared.Utils.Configuration;

public class CdnShardConfiguration
{
    public List<string> Continents { get; set; }
    public string FileMatch { get; set; }
    public Uri CdnFullUrl { get; set; }

    public override string ToString()
    {
        return CdnFullUrl.ToString() + "[" + string.Join(',', Continents) + "] == " + FileMatch;
    }
}