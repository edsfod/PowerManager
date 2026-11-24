using PowerManager.Core.Contracts;
using PowerManager.Core.Logic;
using PowerManager.Core.Models;
using PowerManager.Platform.Ipc;
using PowerManager.Platform.Storage;
using PowerManager.Platform.Win32;

namespace PowerManager.Tests;

public sealed class PlatformStorageAndXmlTests
{
    [Fact]
    public void AppPaths_ParsesExplicitDataDirArg()
    {
        Assert.Equal("C:\\tmp\\pm", AppPaths.ParseDataDirArg(["--status", "--data-dir", "C:\\tmp\\pm"]));
        Assert.Equal("./var", AppPaths.ParseDataDirArg(["--data-dir=./var"]));
        Assert.Null(AppPaths.ParseDataDirArg(["--status"]));
    }

    [Fact]
    public void AtomicFileStore_WritesReadsAndConsumesRequestsInOrder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pm-test-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppPaths(tempRoot);
            paths.EnsureDirectoriesExist();

            var now = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
            AtomicFileStore.WriteJsonAtomic(paths.ModeJsonPath, ModeState.CreateKeepRunning(now, 4));
            var loaded = AtomicFileStore.ReadJsonOrDefault<ModeState>(paths.ModeJsonPath);
            Assert.NotNull(loaded);
            Assert.Equal(AgentMode.KeepRunning, loaded.ParsedMode);

            AtomicFileStore.WriteEcRequestAtomic(paths, new EcTaskRequest(EcTaskRequestKind.Trigger, "电量 35%", null, now));
            AtomicFileStore.WriteEcRequestAtomic(paths, new EcTaskRequest(EcTaskRequestKind.Boost, "UI boost", new BoostRequest(95, 60, 4.0), now.AddSeconds(1)));

            // 写入一个越界非法请求文件，验证消费端拒绝并删除
            File.WriteAllText(
                Path.Combine(paths.RequestsDir, "99999999999999999999-bad.json"),
                """{"kind":"Boost","reason":"bad","boost":{"target_stop":105,"hold_minutes":60,"max_hours":4},"created_at_utc":"2026-11-24T12:00:02Z"}""");

            var rejected = new List<string>();
            var consumed = AtomicFileStore.ConsumeEcRequests(paths, rejected.Add);

            Assert.Equal(2, consumed.Count);
            Assert.Equal(EcTaskRequestKind.Trigger, consumed[0].Kind);
            Assert.Equal(EcTaskRequestKind.Boost, consumed[1].Kind);
            Assert.Single(rejected);
            Assert.Empty(Directory.GetFiles(paths.RequestsDir, "*.json"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void RotatingUtcLogger_RotatesToDot1WhenExceedingLimit()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"pm-log-{Guid.NewGuid():N}");
        try
        {
            string logPath = Path.Combine(tempRoot, "agent.log");
            var logger = new RotatingUtcLogger(logPath, maxBytes: 80);
            var t0 = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

            logger.Write("INFO", new string('A', 60), t0);
            logger.Write("INFO", new string('B', 60), t0.AddSeconds(1));

            Assert.True(File.Exists(logPath + ".1"));
            Assert.True(File.Exists(logPath));
            Assert.Contains("AAAA", File.ReadAllText(logPath + ".1"));
            Assert.Contains("BBBB", File.ReadAllText(logPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void EventLogReaderInterop_ParsesKernelPowerAndTroubleshooterXml()
    {
        const string xml507 = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-Kernel-Power" />
                <EventID>507</EventID>
                <TimeCreated SystemTime="2026-11-24T10:30:00.000Z" />
              </System>
              <EventData>
                <Data Name="Reason">15</Data>
                <Data Name="SleepEntered">true</Data>
                <Data Name="SleepDurationInUs">120000000</Data>
              </EventData>
            </Event>
            """;

        Assert.True(EventLogReaderInterop.TryParseEventXml(xml507, out var e507));
        Assert.NotNull(e507);
        Assert.Equal(PowerLogEventKind.KernelPower507, e507.Kind);
        Assert.Equal(15, e507.Reason);
        Assert.True(e507.SleepEntered);
        Assert.Equal(120_000_000, e507.SleepDurationUs);

        const string xmlPt1 = """
            <Event xmlns="http://schemas.microsoft.com/win/2004/08/events/event">
              <System>
                <Provider Name="Microsoft-Windows-Power-Troubleshooter" />
                <EventID>1</EventID>
                <TimeCreated SystemTime="2026-11-24T10:40:00.000Z" />
              </System>
              <EventData>
                <Data Name="TargetState">5</Data>
                <Data Name="SleepTime">2026-11-24T10:35:00.000Z</Data>
                <Data Name="WakeTime">2026-11-24T10:40:00.000Z</Data>
              </EventData>
            </Event>
            """;

        Assert.True(EventLogReaderInterop.TryParseEventXml(xmlPt1, out var pt1));
        Assert.NotNull(pt1);
        Assert.Equal(PowerLogEventKind.PowerTroubleshooter1, pt1.Kind);
        Assert.Equal(5, pt1.TargetState);
        Assert.Equal(TimeSpan.FromMinutes(5), pt1.WakeTimeUtc!.Value - pt1.SleepTimeUtc!.Value);
    }

    [Theory]
    [InlineData(0x80000000u, true)]
    [InlineData(6u, true)]
    [InlineData(11u, true)]
    [InlineData(13u, true)]
    [InlineData(5u, false)]  // HDMI
    [InlineData(10u, false)] // DisplayPort external
    public void DisplayAndInputInterop_ChecksInternalOutputTechnologyWhitelist(uint tech, bool expectedInternal)
    {
        Assert.Equal(expectedInternal, DisplayAndInputInterop.IsInternalTechnology(tech));
    }

    [Fact]
    public async Task AgentNamedPipe_RoundTripsJsonRequestAndResponse()
    {
        string pipeName = $"PowerManager.Test.{Guid.NewGuid():N}";
        using var server = new AgentNamedPipeServer(
            req => Task.FromResult(new AgentIpcResponse(true, $"echo:{req.Command}")),
            pipeName);

        server.Start();
        var resp = await AgentNamedPipeClient.SendAsync(new AgentIpcRequest(AgentIpcCommand.Ping), pipeName);

        Assert.True(resp.Ok);
        Assert.Equal("echo:Ping", resp.Message);
    }
}
