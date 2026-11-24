using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PowerManager.Core.Contracts;
using PowerManager.Core.Models;
using PowerManager.Platform.Ipc;

namespace PowerManager.Tests;

public sealed class AgentNamedPipeIntegrationTests
{
    private static string NewPipeName() => $"PowerManager.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task RoundTrip_ValidRequests_ReturnStructuredResponses()
    {
        string pipeName = NewPipeName();
        var now = new DateTimeOffset(2026, 11, 24, 15, 0, 0, TimeSpan.Zero);
        var currentState = new ModeState(ModeState.ModeNormalValue, null, now);

        using var server = new AgentNamedPipeServer(req =>
        {
            if (req.Command == AgentIpcCommand.SetMode && req.Mode == AgentMode.KeepRunning)
            {
                int hours = req.DurationHours ?? 12;
                currentState = new ModeState(ModeState.ModeKeepRunningValue, now.AddHours(hours), now);
                return Task.FromResult(new AgentIpcResponse(
                    Ok: true,
                    Message: $"switched to keep_running ({hours}h)",
                    ModeState: currentState,
                    ForcedSleepActive: false,
                    DataDir: @"C:\Temp\PowerManager"));
            }

            return Task.FromResult(new AgentIpcResponse(
                Ok: true,
                ModeState: currentState,
                ForcedSleepActive: true,
                DataDir: @"C:\Temp\PowerManager"));
        }, pipeName);

        server.Start();

        var setResp = await AgentNamedPipeClient.SendAsync(
            new AgentIpcRequest(AgentIpcCommand.SetMode, AgentMode.KeepRunning, 4),
            pipeName,
            timeoutMs: 3000);

        Assert.True(setResp.Ok);
        Assert.Equal("switched to keep_running (4h)", setResp.Message);
        Assert.NotNull(setResp.ModeState);
        Assert.Equal(ModeState.ModeKeepRunningValue, setResp.ModeState!.Mode);
        Assert.Equal(now.AddHours(4), setResp.ModeState.RunUntilUtc);
        Assert.Equal(@"C:\Temp\PowerManager", setResp.DataDir);

        var statusResp = await AgentNamedPipeClient.SendAsync(
            new AgentIpcRequest(AgentIpcCommand.GetStatus),
            pipeName,
            timeoutMs: 3000);

        Assert.True(statusResp.Ok);
        Assert.True(statusResp.ForcedSleepActive);
        Assert.Equal(ModeState.ModeKeepRunningValue, statusResp.ModeState?.Mode);
    }

    [Fact]
    public async Task RoundTrip_HandlerThrows_ReturnsErrorAndKeepsServerAlive()
    {
        string pipeName = NewPipeName();
        int calls = 0;

        using var server = new AgentNamedPipeServer(req =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("simulated handler failure");
            }

            return Task.FromResult(new AgentIpcResponse(Ok: true, Message: "recovered"));
        }, pipeName);

        server.Start();

        var failResp = await AgentNamedPipeClient.SendAsync(
            new AgentIpcRequest(AgentIpcCommand.Ping),
            pipeName,
            timeoutMs: 3000);

        Assert.False(failResp.Ok);
        Assert.Contains("simulated handler failure", failResp.Message);

        var okResp = await AgentNamedPipeClient.SendAsync(
            new AgentIpcRequest(AgentIpcCommand.Ping),
            pipeName,
            timeoutMs: 3000);

        Assert.True(okResp.Ok);
        Assert.Equal("recovered", okResp.Message);
    }

    [Fact]
    public async Task RoundTrip_MalformedJson_ReturnsErrorAndKeepsServerAlive()
    {
        string pipeName = NewPipeName();

        using var server = new AgentNamedPipeServer(
            _ => Task.FromResult(new AgentIpcResponse(Ok: true, Message: "pong")),
            pipeName);

        server.Start();

        using (var rawClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await rawClient.ConnectAsync(3000);
            using var writer = new StreamWriter(rawClient, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(rawClient, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            await writer.WriteLineAsync("{not-valid-json");
            string? line = await reader.ReadLineAsync();
            Assert.False(string.IsNullOrWhiteSpace(line));

            var badResp = JsonSerializer.Deserialize<AgentIpcResponse>(line!);
            Assert.NotNull(badResp);
            Assert.False(badResp!.Ok);
        }

        var okResp = await AgentNamedPipeClient.SendAsync(
            new AgentIpcRequest(AgentIpcCommand.Ping),
            pipeName,
            timeoutMs: 3000);

        Assert.True(okResp.Ok);
        Assert.Equal("pong", okResp.Message);
    }

    [Fact]
    public async Task RoundTrip_SequentialRequests_AllSucceed()
    {
        string pipeName = NewPipeName();
        int counter = 0;

        using var server = new AgentNamedPipeServer(
            _ => Task.FromResult(new AgentIpcResponse(Ok: true, Message: $"seq-{Interlocked.Increment(ref counter)}")),
            pipeName);

        server.Start();

        for (int i = 1; i <= 5; i++)
        {
            var resp = await AgentNamedPipeClient.SendAsync(
                new AgentIpcRequest(AgentIpcCommand.Ping),
                pipeName,
                timeoutMs: 3000);

            Assert.True(resp.Ok);
            Assert.Equal($"seq-{i}", resp.Message);
        }
    }

    [Fact]
    public async Task RoundTrip_ConcurrentClients_AllSucceed()
    {
        string pipeName = NewPipeName();
        int counter = 0;

        using var server = new AgentNamedPipeServer(
            _ => Task.FromResult(new AgentIpcResponse(Ok: true, Message: $"hit-{Interlocked.Increment(ref counter)}")),
            pipeName);

        server.Start();

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => AgentNamedPipeClient.SendAsync(
                new AgentIpcRequest(AgentIpcCommand.Ping),
                pipeName,
                timeoutMs: 3000))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(6, responses.Length);
        Assert.All(responses, r => Assert.True(r.Ok));
        Assert.Equal(6, responses.Select(r => r.Message).Distinct().Count());
    }

    [Fact]
    public async Task Client_MissingPipe_ThrowsTimeoutException()
    {
        string missingPipe = NewPipeName();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            AgentNamedPipeClient.SendAsync(
                new AgentIpcRequest(AgentIpcCommand.Ping),
                missingPipe,
                timeoutMs: 150));
    }
}
