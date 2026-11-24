using PowerManager.Core.Models;

namespace PowerManager.Hardware.MateBook;

/// <summary>
/// MateBook 14 2024（FLMH-XX）OemWMIMethod.OemWMIfun 64 字节输入/输出纯编解码器。
/// 全部字节映射均追溯至 docs/reference/wmi-ec-interface.md。
/// </summary>
public static class MateBookWmiCodec
{
    public const int BufferLength = 64;
    public const int TempSensorCount = 16;

    public static byte[] BuildInput(params byte[] head)
    {
        if (head.Length > BufferLength)
        {
            throw new ArgumentException($"输入头长度不得超过 {BufferLength} 字节。", nameof(head));
        }

        var buffer = new byte[BufferLength];
        Array.Copy(head, buffer, head.Length);
        return buffer;
    }

    public static byte[] BuildSetThresholds(ChargeThresholds target)
    {
        if (target.Start < 0 || target.Stop > 100 || target.Start > target.Stop)
        {
            throw new ArgumentOutOfRangeException(nameof(target), $"无效阈值：{target.Start}-{target.Stop}");
        }

        return BuildInput(0x03, 0x10, (byte)target.Start, (byte)target.Stop);
    }

    public static byte[] BuildGetThresholds() => BuildInput(0x03, 0x11);
    public static byte[] BuildGetResumeFlag() => BuildInput(0x03, 0x13);
    public static byte[] BuildGetChargeMode() => BuildInput(0x03, 0x16);
    public static byte[] BuildGetTempSensor(byte sensorIndex) => BuildInput(0x02, 0x02, sensorIndex);
    public static byte[] BuildGetSystemPower() => BuildInput(0x02, 0x05, 0x00);
    public static byte[] BuildGetBatteryNtc() => BuildInput(0x02, 0x0C, 0x15);
    public static byte[] BuildGetChargeCurrentMa() => BuildInput(0x02, 0x0D, 0x00);

    public static ChargeThresholds? ParseThresholds(byte[] output)
    {
        if (output.Length < 3 || output[0] != 0 || output[1] == 0xFF || output[2] == 0xFF)
        {
            return null;
        }

        return new ChargeThresholds(output[1], output[2]);
    }

    /// <summary>
    /// 解析 02 0C 15 电池 NTC 温度：小端 16 位 0.1 K，raw == 0 视为无效读数返回 null。
    /// </summary>
    public static double? ParseBatteryNtcCelsius(byte[] output)
    {
        if (output.Length < 3 || output[0] != 0)
        {
            return null;
        }

        int raw = output[1] + (256 * output[2]);
        if (raw == 0)
        {
            return null;
        }

        return Math.Round((raw / 10.0) - 273.15, 1);
    }

    public static double? ParseTempSensorCelsius(byte[] output)
    {
        if (output.Length < 4 || output[0] != 0)
        {
            return null;
        }

        double magnitude = output[2] + (output[3] / 10.0);
        return Math.Round(output[1] == 1 ? -magnitude : magnitude, 1);
    }

    public static int? ParseUInt16LE(byte[] output)
    {
        if (output.Length < 3 || output[0] != 0)
        {
            return null;
        }

        return output[1] + (256 * output[2]);
    }
}
