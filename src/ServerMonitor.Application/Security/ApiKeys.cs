using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace ServerMonitor.Application.Security;

public sealed record GeneratedApiKey(string Key, string Prefix, string Hash);

public static class ApiKeys
{
    public const string Scheme = "smk";
    public const int PrefixLength = 16; // "smk_" + 12 hex chars

    private const int LookupIdBytes = 6;
    private const int SecretBytes = 32;

    private static readonly SearchValues<char> HexLower = SearchValues.Create("0123456789abcdef");

    public static GeneratedApiKey Generate()
    {
        var lookupId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(LookupIdBytes));

        Span<byte> secret = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(secret);

        var key = $"{Scheme}_{lookupId}_{Base64Url.EncodeToString(secret)}";
        return new GeneratedApiKey(key, key[..PrefixLength], SecretTokens.Hash(key));
    }

    public static bool TryGetPrefix(string? key, [NotNullWhen(true)] out string? prefix)
    {
        prefix = null;
        if (key is null
            || key.Length <= PrefixLength + 1
            || !key.StartsWith(Scheme + "_", StringComparison.Ordinal)
            || key[PrefixLength] != '_'
            || key.AsSpan(Scheme.Length + 1, PrefixLength - Scheme.Length - 1).ContainsAnyExcept(HexLower))
        {
            return false;
        }

        prefix = key[..PrefixLength];
        return true;
    }

    public static bool Matches(string key, string storedHash) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(SecretTokens.Hash(key)),
            Encoding.ASCII.GetBytes(storedHash));
}
