using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerManager.UI;

/// <summary>
/// WebView2 前端与宿主之间的 JSON-RPC 路由器（9 项桥接方法契约见 docs/design.md §四）。
/// </summary>
internal sealed class WebRpcBridge
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly UiBackendService _backend;
    private readonly Action<bool> _onSetExpanded;
    private readonly Action _onCloseWindow;
    private readonly Action _onSmokeReady;

    public WebRpcBridge(
        UiBackendService backend,
        Action<bool> onSetExpanded,
        Action onCloseWindow,
        Action onSmokeReady)
    {
        _backend = backend;
        _onSetExpanded = onSetExpanded;
        _onCloseWindow = onCloseWindow;
        _onSmokeReady = onSmokeReady;
    }

    public async Task<string?> HandleMessageAsync(string rawJson)
    {
        RpcRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<RpcRequest>(rawJson, JsonOpts);
        }
        catch
        {
            return null;
        }

        if (req is null || string.IsNullOrWhiteSpace(req.Method))
        {
            return null;
        }

        object? result = null;
        string? error = null;

        try
        {
            switch (req.Method)
            {
                case "get_state":
                    result = await _backend.GetStateAsync();
                    break;

                case "set_mode":
                {
                    string mode = req.Params?.GetPropertyOrDefault("mode") ?? "normal";
                    int? hours = req.Params?.GetIntOrDefault("hours");
                    result = await _backend.SetModeAsync(mode, hours);
                    break;
                }

                case "boost":
                {
                    int target = req.Params?.GetIntOrDefault("target") ?? 95;
                    int hold = req.Params?.GetIntOrDefault("hold") ?? 60;
                    double maxh = req.Params?.GetDoubleOrDefault("maxh") ?? 4.0;
                    result = await _backend.RequestBoostAsync(target, hold, maxh);
                    break;
                }

                case "refresh_ec":
                    result = await _backend.RefreshEcAsync();
                    break;

                case "open_data_dir":
                    result = _backend.OpenDataDir();
                    break;

                case "start_agent":
                    result = await _backend.StartAgentAsync();
                    break;

                case "set_expanded":
                {
                    bool expanded = req.Params?.GetBoolOrDefault("expanded") ?? false;
                    _backend.SaveRulesExpandedPref(expanded);
                    _onSetExpanded(expanded);
                    result = new UiActionResult(true);
                    break;
                }

                case "close_window":
                    result = new UiActionResult(true);
                    _onCloseWindow();
                    break;

                case "smoke_ready":
                    result = new UiActionResult(true);
                    _onSmokeReady();
                    break;

                default:
                    error = $"未知方法：{req.Method}";
                    break;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (!req.Id.HasValue)
        {
            return null;
        }

        var resp = new RpcResponse(req.Id.Value, result, error);
        return JsonSerializer.Serialize(resp, JsonOpts);
    }

    private sealed record RpcRequest(
        [property: JsonPropertyName("id")] int? Id,
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("params")] JsonElement? Params);

    private sealed record RpcResponse(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("result")] object? Result,
        [property: JsonPropertyName("error")] string? Error);
}

internal static class JsonElementExtensions
{
    public static string? GetPropertyOrDefault(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    public static int? GetIntOrDefault(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int v) ? v : null;
    }

    public static double? GetDoubleOrDefault(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double v) ? v : null;
    }

    public static bool? GetBoolOrDefault(this JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
