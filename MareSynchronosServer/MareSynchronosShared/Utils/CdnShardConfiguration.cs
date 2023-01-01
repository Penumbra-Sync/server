using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MareSynchronosShared.Utils;

public class CdnShardConfiguration
{
    public string FileMatch { get; set; }
    public Uri CdnFullUrl { get; set; }

    public override string ToString()
    {
        return CdnFullUrl.ToString() + " == " + FileMatch;
    }
}