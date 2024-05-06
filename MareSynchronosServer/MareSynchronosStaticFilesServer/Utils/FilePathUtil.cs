namespace MareSynchronosStaticFilesServer.Utils;

public static partial class FilePathUtil
{
    public static FileInfo GetFileInfoForHash(string basePath, string hash)
    {
        if (hash.Length != 40 || !hash.All(char.IsAsciiLetterOrDigit)) throw new InvalidOperationException();

        FileInfo fi = new(Path.Join(basePath, hash[0].ToString(), hash));
        if (!fi.Exists)
        {
            fi = new FileInfo(Path.Join(basePath, hash));
            if (!fi.Exists)
            {
                return null;
            }
        }

        return fi;
    }

    public static string GetFilePath(string basePath, string hash)
    {
        if (hash.Length != 40 || !hash.All(char.IsAsciiLetterOrDigit)) throw new InvalidOperationException();

        var dirPath = Path.Join(basePath, hash[0].ToString());
        var path = Path.Join(dirPath, hash);
        if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
        return path;
    }
}
