using ServerMonitor.Application.Security;

namespace ServerMonitor.Application.Tests.Security;

public sealed class SecretTokensTests
{
    [Fact]
    public void Generated_tokens_are_prefixed_and_unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => SecretTokens.Generate("abc")).ToList();

        tokens.ShouldAllBe(t => t.StartsWith("abc_", StringComparison.Ordinal) && t.Length == "abc_".Length + 43);
        tokens.Distinct().Count().ShouldBe(tokens.Count);
    }

    [Fact]
    public void Hash_is_deterministic_lowercase_sha256_hex()
    {
        var hash = SecretTokens.Hash("sme_example");

        hash.ShouldBe(SecretTokens.Hash("sme_example"));
        hash.ShouldNotBe(SecretTokens.Hash("sme_example2"));
        hash.ShouldMatch("^[0-9a-f]{64}$");
    }
}
