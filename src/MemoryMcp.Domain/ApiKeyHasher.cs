using System.Security.Cryptography;
using System.Text;

namespace MemoryMcp.Domain;

// Unsalted SHA-256 is deliberate. Keys are 128 bits of randomness, so there is no dictionary to attack and
// nothing for a salt to defend; a slow hash (bcrypt/argon2) would only add latency to every request.
public static class ApiKeyHasher
{
    public static string Hash(string rawKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawKey));
        return Convert.ToHexString(bytes);
    }
}
