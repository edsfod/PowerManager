using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PowerManager.Core.Contracts;

namespace PowerManager.Platform.Ipc;

/// <summary>
/// 单用户 SID ACL 保护的 Named Pipe 服务端/客户端（UI / pmctl ↔ Agent，ADR-0006）。
/// </summary>
public sealed class AgentNamedPipeServer : IDisposable
{
    private readonly string _pipeName;
    private readonly Func<AgentIpcRequest, Task<AgentIpcResponse>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenTask;

    public AgentNamedPipeServer(
        Func<AgentIpcRequest, Task<AgentIpcResponse>> handler,
        string pipeName = IpcConstants.AgentPipeName)
    {
        _handler = handler;
        _pipeName = pipeName;
    }

    public void Start()
    {
        _listenTask = Task.Run(() => ListenLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateCurrentUserPipeServer(_pipeName);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                AgentIpcResponse response;
                try
                {
                    var req = JsonSerializer.Deserialize<AgentIpcRequest>(line);
                    response = req is null
                        ? new AgentIpcResponse(false, "空请求。")
                        : await _handler(req).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response = new AgentIpcResponse(false, ex.Message);
                }

                string respJson = JsonSerializer.Serialize(response);
                await writer.WriteLineAsync(respJson).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 单次管道连接异常不中断常驻监听
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreateCurrentUserPipeServer(string pipeName)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("无法获取当前用户 SID。");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listenTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* 忽略关闭超时 */ }
        _cts.Dispose();
    }
}

public static class AgentNamedPipeClient
{
    public static async Task<AgentIpcResponse> SendAsync(
        AgentIpcRequest request,
        string pipeName = IpcConstants.AgentPipeName,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);

        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
        {
            return new AgentIpcResponse(false, "Agent 未返回有效响应。");
        }

        return JsonSerializer.Deserialize<AgentIpcResponse>(line)
            ?? new AgentIpcResponse(false, "无法解析 Agent 响应。");
    }
}
