using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ServerMonitor.Application.Security;

public static class SecretTokens
{
    public const string EnrollmentPrefix = "sme";

    private const int SecretBytes = 32;

    public static string Generate(string prefix)
    {
        Span<byte> secret = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(secret);
        return $"{prefix}_{Base64Url.EncodeToString(secret)}";
    }

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
