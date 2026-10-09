using ServerMonitor.Application.Security;

namespace ServerMonitor.Application.Tests.Security;

public sealed class ApiKeysTests
{
    [Fact]
    public void Generated_key_has_a_lookup_prefix_and_a_matching_hash()
    {
        var generated = ApiKeys.Generate();

        generated.Key.ShouldMatch("^smk_[0-9a-f]{12}_[A-Za-z0-9_-]{43}$");
        generated.Prefix.ShouldBe(generated.Key[..ApiKeys.PrefixLength]);
        generated.Hash.ShouldBe(SecretTokens.Hash(generated.Key));
        ApiKeys.Matches(generated.Key, generated.Hash).ShouldBeTrue();
    }

    [Fact]
    public void Prefix_is_extracted_even_when_the_secret_contains_underscores()
    {
        const string key = "smk_0123456789ab_se_cr_et";

        ApiKeys.TryGetPrefix(key, out var prefix).ShouldBeTrue();
        prefix.ShouldBe("smk_0123456789ab");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("smk_0123456789ab")]
    [InlineData("smk_0123456789ab_")]
    [InlineData("abc_0123456789ab_secret")]
    [InlineData("smk_0123456789AB_secret")]
    [InlineData("smk_0123456789zz_secret")]
    [InlineData("smk_0123456789a_secret")]
    [InlineData("sme_0123456789ab_secret")]
    public void Malformed_keys_are_rejected(string? key) =>
        ApiKeys.TryGetPrefix(key, out _).ShouldBeFalse();

    [Fact]
    public void A_key_does_not_match_another_keys_hash()
    {
        var first = ApiKeys.Generate();
        var second = ApiKeys.Generate();

        ApiKeys.Matches(first.Key, second.Hash).ShouldBeFalse();
    }
}
