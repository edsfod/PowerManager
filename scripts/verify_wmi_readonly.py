"""Read-only verification script for Huawei MateBook 14 2024 WMI/EC and Windows BatteryStatus.

Strictly read-only: never sends EC write commands (such as 03 10, 03 12, or 03 15).
- Without admin: reads standard root\\wmi BatteryStatus & BatteryFullChargedCapacity.
- With admin: also reads OemWMIMethod.OemWMIfun (03 11, 03 13, 03 16, 02 0C 15, 02 0D 00, 02 05 00, 02 02 0..15).
"""

from __future__ import annotations

import ctypes
import json
import sys

try:
    import comtypes
    import comtypes.client as cc
except ImportError as exc:
    print(f"Missing dependency 'comtypes': {exc}", file=sys.stderr)
    sys.exit(2)


def is_admin() -> bool:
    """Return True if the current process has administrator privileges."""
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def u16_le(out: list[int]) -> int:
    """Decode little-endian 16-bit integer from u8Output[1..2]."""
    return int(out[1]) + 256 * int(out[2])


def query_first(svc: object, cls: str, props: list[str]) -> dict[str, object]:
    """Read first instance of a standard root\\wmi class (no admin required)."""
    for obj in svc.ExecQuery(f"SELECT {', '.join(props)} FROM {cls}"):
        return {p: obj.Properties_.Item(p).Value for p in props}
    return {}


def oem_call(inst: object, meth: object, head: list[int]) -> list[int]:
    """Invoke OemWMIMethod.OemWMIfun with 64-byte zero-padded u8Input and return first 8 output bytes."""
    ip = meth.InParameters.SpawnInstance_()
    ip.Properties_.Item("u8Input").Value = list(head) + [0] * (64 - len(head))
    out = inst.ExecMethod_("OemWMIfun", ip)
    return [int(x) for x in list(out.Properties_.Item("u8Output").Value)[:8]]


def main() -> int:
    comtypes.CoInitialize()
    loc = cc.CreateObject("WbemScripting.SWbemLocator", dynamic=True)
    svc = loc.ConnectServer(".", r"root\wmi")

    bat = query_first(svc, "BatteryStatus", ["PowerOnline", "Charging", "ChargeRate", "DischargeRate", "RemainingCapacity"])
    full = query_first(svc, "BatteryFullChargedCapacity", ["FullChargedCapacity"])

    result: dict[str, object] = {
        "is_admin": is_admin(),
        "windows_battery": {**bat, **full},
    }

    if not result["is_admin"]:
        result["ec_wmi"] = "Skipped OemWMIMethod.OemWMIfun (run as Administrator to query EC registers)."
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0

    try:
        inst = list(svc.InstancesOf("OemWMIMethod"))[0]
        meth = svc.Get("OemWMIMethod").Methods_.Item("OemWMIfun")
    except Exception as exc:
        result["ec_wmi_error"] = f"Failed to bind OemWMIMethod: {exc}"
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 1

    gbtt = oem_call(inst, meth, [0x03, 0x11])
    gbac = oem_call(inst, meth, [0x03, 0x13])
    gbcm = oem_call(inst, meth, [0x03, 0x16])
    ntc = oem_call(inst, meth, [0x02, 0x0C, 0x15])
    chg = oem_call(inst, meth, [0x02, 0x0D, 0x00])
    sysp = oem_call(inst, meth, [0x02, 0x05, 0x00])

    temps: dict[str, float] = {}
    for n in range(16):
        o = oem_call(inst, meth, [0x02, 0x02, n])
        if o[0] == 0:
            v = o[2] + o[3] / 10.0
            temps[str(n)] = round(-v if o[1] == 1 else v, 1)

    result["ec_wmi"] = {
        "gbtt_raw": gbtt[:3],
        "hw_ok": gbtt[0] == 0 and gbtt[1] != 0xFF and gbtt[2] != 0xFF,
        "hw_start_pct": gbtt[1],
        "hw_stop_pct": gbtt[2],
        "gbac_raw": gbac[1],
        "gbcm_raw": gbcm[:5],
        "charge_ma": u16_le(chg) if chg[0] == 0 else None,
        "system_w": sysp[1] if sysp[0] == 0 else None,
        "battery_ntc_c": round(u16_le(ntc) / 10.0 - 273.15, 1) if ntc[0] == 0 and u16_le(ntc) else None,
        "sensors_c": temps,
    }

    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
