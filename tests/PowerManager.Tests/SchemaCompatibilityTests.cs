using System.Text.Json;
using PowerManager.Core.Contracts;
using PowerManager.Core.Models;

namespace PowerManager.Tests;

public sealed class SchemaCompatibilityTests
{
    [Fact]
    public void OriginalPowerSettings_DeserializesV1JsonSchema()
    {
        const string v1Json = """
            {
              "ac": {
                "display": 18000,
                "sleep": 18000,
                "lid": 0,
                "button": 0
              },
              "dc": {
                "display": 3600,
                "sleep": 3600,
                "lid": 0,
                "button": 0
              },
              "saved_at_utc": "2026-11-22T06:19:11Z"
            }
            """;

        var parsed = JsonSerializer.Deserialize<OriginalPowerSettings>(v1Json);
        Assert.NotNull(parsed);
        Assert.Equal(18000, parsed.Ac.Display);
        Assert.Equal(18000, parsed.Ac.Sleep);
        Assert.Equal(3600, parsed.Dc.Display);
        Assert.Equal(3600, parsed.Dc.Sleep);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 6, 19, 11, TimeSpan.Zero), parsed.SavedAtUtc);
    }

    [Fact]
    public void ModeState_DeserializesV1JsonSchema()
    {
        const string v1ModeJson = """
            {
              "mode": "normal",
              "run_until_utc": null,
              "changed_at_utc": "2026-11-23T15:55:00Z"
            }
            """;

        var parsed = JsonSerializer.Deserialize<ModeState>(v1ModeJson);
        Assert.NotNull(parsed);
        Assert.Equal(AgentMode.Normal, parsed.ParsedMode);
        Assert.Null(parsed.RunUntilUtc);
    }

    [Fact]
    public void EcTaskRequest_EnforcesStrictWhitelistValidation()
    {
        var now = DateTimeOffset.UtcNow;

        var validTrigger = new EcTaskRequest(EcTaskRequestKind.Trigger, "电量 35%", null, now);
        Assert.True(validTrigger.TryValidate(out _));

        var validBoost = new EcTaskRequest(EcTaskRequestKind.Boost, "UI boost", new BoostRequest(95, 60, 4.0), now);
        Assert.True(validBoost.TryValidate(out _));

        var invalidTriggerWithBoost = new EcTaskRequest(EcTaskRequestKind.Trigger, "bad", new BoostRequest(95, 60, 4.0), now);
        Assert.False(invalidTriggerWithBoost.TryValidate(out _));

        var invalidBoostMissingPayload = new EcTaskRequest(EcTaskRequestKind.Boost, "bad", null, now);
        Assert.False(invalidBoostMissingPayload.TryValidate(out _));

        var invalidEmptyReason = new EcTaskRequest(EcTaskRequestKind.Trigger, "   ", null, now);
        Assert.False(invalidEmptyReason.TryValidate(out _));

        var invalidBoostBounds = new EcTaskRequest(EcTaskRequestKind.Boost, "bad", new BoostRequest(80, 60, 4.0), now);
        Assert.False(invalidBoostBounds.TryValidate(out _));
    }
}
