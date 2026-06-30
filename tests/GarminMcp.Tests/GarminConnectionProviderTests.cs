using GarminMcp.Core;
using GarminMcp.Core.Auth;
using Xunit;

namespace GarminMcp.Tests;

public class GarminConnectionProviderTests
{
    private const string SetupUrl = "http://localhost:8765/";

    private static GarminConnectionProvider Unauthenticated() =>
        new(GarminClientFactory.CreateHttpClient(), new GarminOptions(), SetupUrl);

    [Fact]
    public void StartsUnauthenticated_WhenNoCredentials()
    {
        var provider = Unauthenticated();
        Assert.False(provider.IsAuthenticated);
        Assert.Equal(SetupUrl, provider.SetupUrl);
    }

    [Fact]
    public async Task Service_Throws_NotAuthenticated_WithSetupUrl()
    {
        var provider = Unauthenticated();
        var ex = await Assert.ThrowsAsync<GarminNotAuthenticatedException>(
            () => provider.Service.GetProfileAsync());
        Assert.Equal(SetupUrl, ex.SetupUrl);
        Assert.Contains(SetupUrl, ex.Message);
    }

    [Fact]
    public async Task InitializeAsync_DoesNotThrow_WithNoConfig()
    {
        var provider = Unauthenticated();
        await provider.InitializeAsync();
        Assert.False(provider.IsAuthenticated);
    }

    [Theory]
    [InlineData("", "pw")]
    [InlineData("a@b.c", "")]
    public async Task BeginLogin_Errors_OnMissingCredentials(string email, string password)
    {
        var provider = Unauthenticated();
        var outcome = await provider.BeginLoginAsync(email, password);
        Assert.Equal(LoginStatus.Error, outcome.Status);
    }

    [Fact]
    public async Task SubmitMfa_Errors_WhenNoPendingLogin()
    {
        var provider = Unauthenticated();
        var outcome = await provider.SubmitMfaAsync("123456");
        Assert.Equal(LoginStatus.Error, outcome.Status);
    }
}
