using System.Text.RegularExpressions;
using GarminMcp.Core.Auth;
using Xunit;

namespace GarminMcp.Tests;

public class OAuth1SignerTests
{
    // The canonical Twitter "Creating a signature" example. If our signer reproduces
    // this exact signature, the base-string construction, percent-encoding, parameter
    // sorting and HMAC-SHA1 are all correct — which is what Garmin's OAuth1 needs too.
    [Fact]
    public void BuildAuthorizationHeader_MatchesCanonicalTwitterVector()
    {
        var header = OAuth1Signer.BuildAuthorizationHeader(
            method: "POST",
            url: "https://api.twitter.com/1/statuses/update.json?include_entities=true",
            consumerKey: "xvz1evFS4wEEPTGEFPHBog",
            consumerSecret: "kAcSOqF21Fu85e7zjz7ZN2U4ZRhfV3WpwPAoE3Y7iD",
            token: "370773112-GmHxMAgYyLbNEtIKZeRNFsMKPR9EyMZeS9weJAEb",
            tokenSecret: "LswwdoUaIVS3MWWqrwh1QqK3VL5DvY5oZqfYQDcs7yug",
            extraParameters: new Dictionary<string, string>
            {
                ["status"] = "Hello Ladies + Gentlemen, a signed OAuth request!",
            },
            timestamp: "1318622958",
            nonce: "kYjzVBB8Y0ZFabxSWbWovY3uYSQ2pTgmZeNu2VS4cg");

        // Ground truth = HMAC-SHA1 of Twitter's documented signature base string with the
        // documented key (verified independently). This pins base-string construction,
        // double percent-encoding, parameter sorting and the HMAC.
        var signature = ExtractParam(header, "oauth_signature");
        Assert.Equal("rjG4aZ5SnoDdiF7430p0tHPd5tU=", signature);
    }

    [Fact]
    public void BuildAuthorizationHeader_IncludesExpectedOauthParameters()
    {
        var header = OAuth1Signer.BuildAuthorizationHeader(
            "GET", "https://connectapi.garmin.com/oauth-service/oauth/preauthorized?ticket=ST-123",
            "key", "secret", timestamp: "1700000000", nonce: "abc123");

        Assert.StartsWith("OAuth ", header);
        Assert.Equal("key", ExtractParam(header, "oauth_consumer_key"));
        Assert.Equal("HMAC-SHA1", ExtractParam(header, "oauth_signature_method"));
        Assert.Equal("1.0", ExtractParam(header, "oauth_version"));
        Assert.Equal("1700000000", ExtractParam(header, "oauth_timestamp"));
        Assert.Equal("abc123", ExtractParam(header, "oauth_nonce"));
        // Two-legged request: no oauth_token.
        Assert.DoesNotContain("oauth_token=", header);
    }

    [Theory]
    [InlineData("Ladies + Gentlemen", "Ladies%20%2B%20Gentlemen")]
    [InlineData("hCtSmYh+iHYCEqBWrE7C7hYmtUk=", "hCtSmYh%2BiHYCEqBWrE7C7hYmtUk%3D")]
    [InlineData("safe-._~AZ09", "safe-._~AZ09")]
    public void Encode_PercentEncodesPerRfc3986(string input, string expected)
    {
        Assert.Equal(expected, OAuth1Signer.Encode(input));
    }

    private static string ExtractParam(string header, string name)
    {
        var match = Regex.Match(header, $"{Regex.Escape(name)}=\"([^\"]*)\"");
        Assert.True(match.Success, $"Header missing {name}: {header}");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}
