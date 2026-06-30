using GarminMcp.Core;
using Garmin.Connect;
using Garmin.Connect.Auth.External;
using Garmin.Connect.Exceptions;
using Garmin.Connect.Models;
using NSubstitute;
using Xunit;

namespace GarminMcp.Tests;

public class GarminServiceTests
{
    private readonly IGarminConnectClient _client = Substitute.For<IGarminConnectClient>();
    private readonly GarminService _service;

    public GarminServiceTests() => _service = new GarminService(_client);

    [Fact]
    public async Task GetDailySummary_ParsesIsoDate_AndCallsClient()
    {
        _client.GetUserSummary(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new GarminStats());

        await _service.GetDailySummaryAsync("2026-06-30");

        await _client.Received(1).GetUserSummary(
            new DateTime(2026, 6, 30), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("2026-13-01")]
    [InlineData("30-06-2026")]
    [InlineData("not-a-date")]
    [InlineData("2026/06/30")]
    public async Task GetDailySummary_Throws_OnInvalidDate(string date)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetDailySummaryAsync(date));
    }

    [Fact]
    public async Task BodyBattery_DefaultsEndToStart_WhenEndOmitted()
    {
        _client.GetWelnessBodyBatteryData(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GarminBodyBatteryData>());

        await _service.GetBodyBatteryAsync("2026-06-01");

        await _client.Received(1).GetWelnessBodyBatteryData(
            new DateTime(2026, 6, 1), new DateTime(2026, 6, 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Range_Throws_WhenEndBeforeStart()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.GetWeightAsync("2026-06-10", "2026-06-01"));
    }

    [Fact]
    public async Task Activities_Validates_Limit()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetActivitiesAsync(0, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetActivitiesAsync(0, 101));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GetActivitiesAsync(-1, 10));
    }

    [Fact]
    public async Task RateLimit_IsMappedTo_GarminServiceException()
    {
        _client.GetUserSummary(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GarminStats>(new GarminConnectTooManyRequestsException()));

        var ex = await Assert.ThrowsAsync<GarminServiceException>(
            () => _service.GetDailySummaryAsync("2026-06-30"));
        Assert.Contains("rate-limited", ex.Message);
    }

    [Fact]
    public async Task AuthFailure_IsMappedTo_GarminServiceException()
    {
        _client.GetWellnessSleepData(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GarminSleepData>(
                new GarminConnectAuthenticationException("boom") { Code = Code.OAuth2TokenNotFound }));

        var ex = await Assert.ThrowsAsync<GarminServiceException>(
            () => _service.GetSleepAsync("2026-06-30"));
        Assert.Contains("authentication failed", ex.Message);
    }

    [Fact]
    public async Task PersonalRecords_UsesDisplayNameFromProfile()
    {
        _client.GetSocialProfile(Arg.Any<CancellationToken>())
            .Returns(new GarminSocialProfile { DisplayName = "athlete-42" });
        _client.GetPersonalRecord("athlete-42", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GarminPersonalRecord>());

        await _service.GetPersonalRecordsAsync();

        await _client.Received(1).GetPersonalRecord("athlete-42", Arg.Any<CancellationToken>());
    }
}
