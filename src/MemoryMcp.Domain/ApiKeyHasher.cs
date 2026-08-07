using System.Security.Cryptography;
using System.Text;

namespace MemoryMcp.Domain;

public static class ApiKeyHasher
{
    public static string Hash(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes);
    }
}
