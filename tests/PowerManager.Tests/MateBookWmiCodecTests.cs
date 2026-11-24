using PowerManager.Core.Models;
using PowerManager.Hardware.MateBook;

namespace PowerManager.Tests;

public sealed class MateBookWmiCodecTests
{
    [Fact]
    public void BuildSetThresholds_Creates64ByteBufferWith0310Header()
    {
        byte[] buf = MateBookWmiCodec.BuildSetThresholds(new ChargeThresholds(75, 80));
        Assert.Equal(64, buf.Length);
        Assert.Equal(0x03, buf[0]);
        Assert.Equal(0x10, buf[1]);
        Assert.Equal(75, buf[2]);
        Assert.Equal(80, buf[3]);
        Assert.All(buf.Skip(4), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ParseThresholds_ValidAndInvalidCases()
    {
        Assert.Equal(new ChargeThresholds(75, 80), MateBookWmiCodec.ParseThresholds([0, 75, 80, 0]));
        Assert.Null(MateBookWmiCodec.ParseThresholds([1, 75, 80]));
        Assert.Null(MateBookWmiCodec.ParseThresholds([0, 0xFF, 80]));
        Assert.Null(MateBookWmiCodec.ParseThresholds([0, 75, 0xFF]));
    }

    [Fact]
    public void ParseBatteryNtcCelsius_ZeroRawReturnsNull_ValidRawConvertsKelvinToCelsius()
    {
        // 原始值为 0 视为无效读数（docs/reference/wmi-ec-interface.md）
        Assert.Null(MateBookWmiCodec.ParseBatteryNtcCelsius([0, 0, 0]));
        Assert.Null(MateBookWmiCodec.ParseBatteryNtcCelsius([1, 0xE6, 0x0B]));

        // 3042 (0.1 K) = 304.2 K -> 304.2 - 273.15 = 31.05 °C (IEEE-754 -> 31.1 °C)
        int raw = 3042; // 0x0BE2
        byte[] output = [0, (byte)(raw & 0xFF), (byte)(raw >> 8)];
        Assert.Equal(31.1, MateBookWmiCodec.ParseBatteryNtcCelsius(output)!.Value, precision: 1);
    }

    [Fact]
    public void ParseTempSensorCelsius_HandlesPositiveAndNegative()
    {
        Assert.Equal(47.1, MateBookWmiCodec.ParseTempSensorCelsius([0, 0, 47, 1]));
        Assert.Equal(-5.3, MateBookWmiCodec.ParseTempSensorCelsius([0, 1, 5, 3]));
        Assert.Null(MateBookWmiCodec.ParseTempSensorCelsius([2, 0, 47, 1]));
    }

    [Fact]
    public void ParseUInt16LE_ParsesChargeCurrentMa()
    {
        // 1984 mA = 0x07C0 -> LE [0xC0, 0x07]
        Assert.Equal(1984, MateBookWmiCodec.ParseUInt16LE([0, 0xC0, 0x07]));
        Assert.Null(MateBookWmiCodec.ParseUInt16LE([1, 0xC0, 0x07]));
    }
}
