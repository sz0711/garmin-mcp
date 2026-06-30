using GarminMcp.Core.Auth;
using Garmin.Connect.Auth;
using Xunit;

namespace GarminMcp.Tests;

public class GarminTokenBundleTests
{
    private static GarminTokenBundle Sample() => new()
    {
        Oauth1 = new OAuth1Token { Token = "tok", TokenSecret = "sec", Domain = "garmin.com" },
        Oauth2 = new OAuth2Token { AccessToken = "acc", RefreshToken = "ref", TokenType = "Bearer", ExpiresIn = 3600 },
    };

    [Fact]
    public void Base64_RoundTrips()
    {
        var bundle = Sample();
        var restored = GarminTokenBundle.Parse(bundle.ToBase64());

        Assert.Equal("tok", restored.Oauth1.Token);
        Assert.Equal("sec", restored.Oauth1.TokenSecret);
        Assert.Equal("garmin.com", restored.Oauth1.Domain);
        Assert.NotNull(restored.Oauth2);
        Assert.Equal("acc", restored.Oauth2!.AccessToken);
        Assert.Equal(3600, restored.Oauth2.ExpiresIn);
    }

    [Fact]
    public void Json_RoundTrips_And_ParseAutoDetects()
    {
        var bundle = Sample();
        var json = bundle.ToJson();

        Assert.StartsWith("{", json);
        var restored = GarminTokenBundle.Parse(json);
        Assert.Equal("tok", restored.Oauth1.Token);
    }

    [Fact]
    public void Json_UsesOAuth2SnakeCaseFieldNames()
    {
        var json = Sample().ToJson();
        Assert.Contains("\"access_token\"", json);
        Assert.Contains("\"refresh_token\"", json);
    }

    [Fact]
    public void Oauth2_IsOptional()
    {
        var bundle = new GarminTokenBundle
        {
            Oauth1 = new OAuth1Token { Token = "t", TokenSecret = "s" },
        };
        var restored = GarminTokenBundle.Parse(bundle.ToBase64());
        Assert.Null(restored.Oauth2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64-not-json-!!!")]
    public void Parse_Throws_OnGarbage(string value)
    {
        Assert.Throws<FormatException>(() => GarminTokenBundle.Parse(value));
    }
}
