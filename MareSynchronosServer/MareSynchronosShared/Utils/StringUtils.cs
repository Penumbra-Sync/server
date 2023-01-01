using System.Security.Cryptography;
using System.Text;

namespace MareSynchronosShared.Utils;

public static class StringUtils
{
    public static string GenerateRandomString(int length, string? allowableChars = null)
    {
        if (string.IsNullOrEmpty(allowableChars))
            allowableChars = @"ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";

        // Generate random data
        var rnd = RandomNumberGenerator.GetBytes(length);

        // Generate the output string
        var allowable = allowableChars.ToCharArray();
        var l = allowable.Length;
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = allowable[rnd[i] % l];

        return new string(chars);
    }

    public static string Sha256String(string input)
    {
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(input))).Replace("-", "", StringComparison.OrdinalIgnoreCase);
    }
}

public static class ListUtils
{
    private static Random rng = new();

    public static void Shuffle<T>(this IList<T> list)
    {
        int n = list.Count;
        while (n > 1)
        {
            n--;
            int k = rng.Next(n + 1);
            T value = list[k];
            list[k] = list[n];
            list[n] = value;
        }
    }
}