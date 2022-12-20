namespace MareSynchronosStaticFilesServer;

public static class FilePathUtil
{
    public static FileInfo? GetFileInfoForHash(string basePath, string hash)
    {
        FileInfo fi = new(Path.Combine(basePath, hash[0].ToString(), hash));
        if (!fi.Exists)
        {
            fi = new FileInfo(Path.Combine(basePath, hash));
            if (!fi.Exists)
            {
                return null;
            }
        }

        return fi;
    }

    public static string GetFilePath(string basePath, string hash)
    {
        var dirPath = Path.Combine(basePath, hash[0].ToString());
        var path = Path.Combine(dirPath, hash);
        if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
        return path;
    }
}
