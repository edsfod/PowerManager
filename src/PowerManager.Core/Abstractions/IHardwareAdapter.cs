using PowerManager.Core.Models;

namespace PowerManager.Core.Abstractions;

/// <summary>
/// 底层硬件适配抽象接口（遵循 ADR-0001：上层不感知具体 WMI/EC 字节）。
/// </summary>
public interface IChargeHardwareAdapter
{
    /// <summary>读取当前 EC 启停充电阈值；读数异常返回 null。</summary>
    ChargeThresholds? ReadChargeThresholds();

    /// <summary>
    /// 写入 EC 启停充电阈值，并必须执行读回校验；读回不一致时抛出 <see cref="EcThresholdMismatchException"/>。
    /// </summary>
    void WriteChargeThresholdsAndVerify(ChargeThresholds target);

    /// <summary>读取当前 EC 传感器、功耗与电池 NTC 温度（原始值为 0 时 BatteryC 必须为 null）。</summary>
    EcTelemetry ReadEcTelemetry();

    /// <summary>不提权读取 Windows 标准 WMI BatteryStatus 与满充容量。</summary>
    WindowsBatteryTelemetry? ReadWindowsBatteryTelemetry();
}

public sealed class EcThresholdMismatchException(ChargeThresholds expected, ChargeThresholds? actual)
    : Exception($"EC threshold MISMATCH: expected {expected.Start}-{expected.Stop}, read back {(actual is null ? "INVALID" : $"{actual.Start}-{actual.Stop}")}.")
{
    public ChargeThresholds Expected { get; } = expected;
    public ChargeThresholds? Actual { get; } = actual;
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
