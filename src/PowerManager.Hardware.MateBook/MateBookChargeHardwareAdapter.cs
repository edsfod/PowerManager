using System.Management;
using PowerManager.Core.Abstractions;
using PowerManager.Core.Models;

namespace PowerManager.Hardware.MateBook;

/// <summary>
/// 华为 MateBook 14 2024（FLMH-XX）WMI/EC 硬件适配器。
/// OemWMIMethod.OemWMIfun 需管理员权限；BatteryStatus 与 BatteryFullChargedCapacity 无需管理员权限。
/// </summary>
public sealed class MateBookChargeHardwareAdapter : IChargeHardwareAdapter
{
    private const string WmiNamespace = @"\\.\root\wmi";
    private const string OemClassName = "OemWMIMethod";
    private const string OemMethodName = "OemWMIfun";

    public ChargeThresholds? ReadChargeThresholds()
    {
        byte[] output = InvokeOemMethod(MateBookWmiCodec.BuildGetThresholds());
        return MateBookWmiCodec.ParseThresholds(output);
    }

    /// <summary>
    /// 写入 03 10 充电阈值后强制再读 03 11 校验一致；读回不一致抛出 EcThresholdMismatchException。
    /// </summary>
    public void WriteChargeThresholdsAndVerify(ChargeThresholds target)
    {
        byte[] setOutput = InvokeOemMethod(MateBookWmiCodec.BuildSetThresholds(target));
        if (setOutput.Length == 0 || setOutput[0] != 0)
        {
            byte code = setOutput.Length > 0 ? setOutput[0] : (byte)0xFF;
            throw new IOException($"EC 03 10 写入失败，状态码={code}。");
        }

        ChargeThresholds? readBack = ReadChargeThresholds();
        if (readBack is null || readBack != target)
        {
            throw new EcThresholdMismatchException(target, readBack);
        }
    }

    public EcTelemetry ReadEcTelemetry()
    {
        var sensors = new Dictionary<string, double>();
        for (byte n = 0; n < MateBookWmiCodec.TempSensorCount; n++)
        {
            double? temp = MateBookWmiCodec.ParseTempSensorCelsius(InvokeOemMethod(MateBookWmiCodec.BuildGetTempSensor(n)));
            if (temp.HasValue)
            {
                sensors[n.ToString()] = temp.Value;
            }
        }

        byte[] gbtt = InvokeOemMethod(MateBookWmiCodec.BuildGetThresholds());
        byte[] gbcm = InvokeOemMethod(MateBookWmiCodec.BuildGetChargeMode());
        byte[] gbac = InvokeOemMethod(MateBookWmiCodec.BuildGetResumeFlag());
        byte[] chg = InvokeOemMethod(MateBookWmiCodec.BuildGetChargeCurrentMa());
        byte[] sysp = InvokeOemMethod(MateBookWmiCodec.BuildGetSystemPower());
        byte[] ntc = InvokeOemMethod(MateBookWmiCodec.BuildGetBatteryNtc());

        ChargeThresholds? thresholds = MateBookWmiCodec.ParseThresholds(gbtt);

        return new EcTelemetry(
            HwOk: thresholds is not null,
            HwStart: thresholds?.Start ?? (gbtt.Length > 1 ? gbtt[1] : null),
            HwStop: thresholds?.Stop ?? (gbtt.Length > 2 ? gbtt[2] : null),
            ChargeModeRaw: gbcm.Take(5).Select(b => (int)b).ToArray(),
            ResumeFlagRaw: gbac.Length > 1 ? gbac[1] : null,
            ChargeMa: MateBookWmiCodec.ParseUInt16LE(chg),
            SystemW: sysp.Length > 1 && sysp[0] == 0 ? sysp[1] : null,
            BatteryC: MateBookWmiCodec.ParseBatteryNtcCelsius(ntc),
            SensorsC: sensors,
            HottestC: sensors.Count > 0 ? sensors.Values.Max() : null);
    }

    public WindowsBatteryTelemetry? ReadWindowsBatteryTelemetry()
    {
        var scope = new ManagementScope(WmiNamespace);
        scope.Connect();

        bool online = false;
        bool charging = false;
        int chargeMw = 0;
        int dischargeMw = 0;
        int remainingMwh = 0;
        bool foundBattery = false;

        using (var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT PowerOnline, Charging, ChargeRate, DischargeRate, RemainingCapacity FROM BatteryStatus")))
        using (var collection = searcher.Get())
        {
            foreach (ManagementBaseObject obj in collection)
            {
                online = Convert.ToBoolean(obj["PowerOnline"]);
                charging = Convert.ToBoolean(obj["Charging"]);
                chargeMw = Convert.ToInt32(obj["ChargeRate"]);
                dischargeMw = Convert.ToInt32(obj["DischargeRate"]);
                remainingMwh = Convert.ToInt32(obj["RemainingCapacity"]);
                foundBattery = true;
                break;
            }
        }

        if (!foundBattery)
        {
            return null;
        }

        int fullMwh = 0;
        using (var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")))
        using (var collection = searcher.Get())
        {
            foreach (ManagementBaseObject obj in collection)
            {
                fullMwh = Convert.ToInt32(obj["FullChargedCapacity"]);
                break;
            }
        }

        return new WindowsBatteryTelemetry(
            Online: online,
            Charging: charging,
            ChargeMw: chargeMw,
            DischargeMw: dischargeMw,
            RemainingMwh: remainingMwh,
            FullMwh: fullMwh);
    }

    private static byte[] InvokeOemMethod(byte[] input64)
    {
        var scope = new ManagementScope(WmiNamespace);
        scope.Connect();

        using var oemClass = new ManagementClass(scope, new ManagementPath(OemClassName), null);
        using var instances = oemClass.GetInstances();
        foreach (ManagementObject instance in instances)
        {
            using (instance)
            {
                using ManagementBaseObject inParams = instance.GetMethodParameters(OemMethodName);
                inParams["u8Input"] = input64;

                using ManagementBaseObject outParams = instance.InvokeMethod(OemMethodName, inParams, null);
                if (outParams["u8Output"] is byte[] bytes)
                {
                    return bytes;
                }

                return [];
            }
        }

        throw new InvalidOperationException($"未找到 WMI 实例 {WmiNamespace}:{OemClassName}。");
    }
}
