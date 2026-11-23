using System.Security.Cryptography;

namespace Strela.Engine.Tests;

public static class TestData
{
    public static byte[] Random(int size, int seed)
    {
        var data = new byte[size];
        new System.Random(seed).NextBytes(data);
        return data;
    }

    public static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
