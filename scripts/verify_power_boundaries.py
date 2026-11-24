"""Standalone verification script for Windows Modern Standby, power button, and sleep API boundaries.

Uses only Python standard library + native Windows CLI tools (powercfg, wevtutil)
and Win32 powrprof.dll. Does not depend on PowerManager code.
"""

from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import locale
import multiprocessing
import subprocess
import sys
import tempfile
import threading
import time
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

EVENT_NS = {"e": "http://schemas.microsoft.com/win/2004/08/events/event"}


class _LUID(ctypes.Structure):
    _fields_ = [("Low", wt.DWORD), ("High", wt.LONG)]


class _TOKEN_PRIVILEGES(ctypes.Structure):
    _fields_ = [("Count", wt.DWORD), ("Luid", _LUID), ("Attr", wt.DWORD)]


def enable_shutdown_privilege() -> bool:
    """Enable SeShutdownPrivilege on the current process token (required by SetSuspendState)."""
    advapi = ctypes.WinDLL("advapi32")
    k32 = ctypes.WinDLL("kernel32")
    advapi.OpenProcessToken.argtypes = [wt.HANDLE, wt.DWORD, ctypes.POINTER(wt.HANDLE)]
    advapi.LookupPrivilegeValueW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p]
    advapi.AdjustTokenPrivileges.argtypes = [
        wt.HANDLE, wt.BOOL, ctypes.c_void_p, wt.DWORD, ctypes.c_void_p, ctypes.c_void_p,
    ]
    k32.GetCurrentProcess.restype = wt.HANDLE

    tok = wt.HANDLE()
    if not advapi.OpenProcessToken(k32.GetCurrentProcess(), 0x0020 | 0x0008, ctypes.byref(tok)):
        return False
    try:
        tp = _TOKEN_PRIVILEGES(1, _LUID(), 0x2)
        ok = (
            advapi.LookupPrivilegeValueW(None, "SeShutdownPrivilege", ctypes.byref(tp.Luid))
            and advapi.AdjustTokenPrivileges(tok, False, ctypes.byref(tp), 0, None, None)
        )
        return bool(ok)
    finally:
        k32.CloseHandle(tok)


def run_cmd(args: list[str], encoding: str | None = None) -> tuple[int, str, str]:
    """Run a command and return (returncode, stdout, stderr) using system encoding by default."""
    enc = encoding or locale.getpreferredencoding(False)
    proc = subprocess.run(args, capture_output=True, text=True, encoding=enc, errors="replace")
    return proc.returncode, proc.stdout, proc.stderr


def parse_sleep_states(out: str) -> tuple[bool, bool]:
    """Parse powercfg /a output and return (has_s0, has_s3) from the available section only."""
    available_lines: list[str] = []
    for line in out.splitlines():
        low = line.lower()
        if "not available" in low or "不可用" in low:
            break
        available_lines.append(low)
    available = "\n".join(available_lines)
    has_s0 = "s0 low power idle" in available or "s0 低功耗空闲" in available
    has_s3 = "standby (s3)" in available or "待机 (s3)" in available
    return has_s0, has_s3


def cmd_check_s3(_args: argparse.Namespace) -> None:
    """Check firmware sleep states via powercfg /a."""
    _rc, stdout, stderr = run_cmd(["powercfg", "/a"])
    out = stdout + ("\n" + stderr if stderr.strip() else "")
    print(out.strip())
    has_s0, has_s3 = parse_sleep_states(stdout)
    print("-" * 50)
    print(f"Modern Standby (S0): {has_s0}")
    print(f"Traditional S3 available: {has_s3}")


def query_recent_power_events(limit: int = 8) -> list[dict[str, str]]:
    """Query recent Kernel-Power (42, 506, 507) and Power-Troubleshooter (1) events."""
    query = (
        "*[System[(Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=506 or EventID=507)) "
        "or (Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)]]"
    )
    rc, xml_out, stderr = run_cmd(
        ["wevtutil", "qe", "System", f"/q:{query}", "/f:xml", f"/c:{limit}", "/rd:true"],
        encoding="utf-8",
    )
    if rc != 0:
        print(f"wevtutil failed (exit {rc}): {stderr.strip()}", file=sys.stderr)
        return []
    if not xml_out.strip():
        return []

    wrapped = f"<Events>{xml_out}</Events>"
    try:
        root = ET.fromstring(wrapped)
    except ET.ParseError as exc:
        print(f"Failed to parse wevtutil XML: {exc}")
        return []

    events: list[dict[str, str]] = []
    for ev in root.findall("e:Event", EVENT_NS):
        sys_node = ev.find("e:System", EVENT_NS)
        if sys_node is None:
            continue
        provider = sys_node.find("e:Provider", EVENT_NS)
        event_id = sys_node.find("e:EventID", EVENT_NS)
        time_created = sys_node.find("e:TimeCreated", EVENT_NS)

        data_map: dict[str, str] = {}
        for d in ev.findall("e:EventData/e:Data", EVENT_NS):
            name = d.attrib.get("Name", "")
            if name:
                data_map[name] = d.text or ""

        events.append({
            "time_utc": time_created.attrib.get("SystemTime", "") if time_created is not None else "",
            "provider": provider.attrib.get("Name", "") if provider is not None else "",
            "id": event_id.text or "" if event_id is not None else "",
            "data": ", ".join(f"{k}={v}" for k, v in data_map.items() if k in {
                "Reason", "SleepEntered", "SleepDurationInUs", "TargetState", "SleepTime", "WakeTime"
            }),
        })
    return events


def print_recent_events(limit: int = 8) -> None:
    """Print recent power events in a compact table."""
    events = query_recent_power_events(limit=limit)
    print(f"\n=== Recent Power Events (last {len(events)}, UTC) ===")
    for ev in events:
        print(f"[{ev['time_utc']}] {ev['provider']} #{ev['id']} | {ev['data']}")


def heartbeat_worker(log_path: Path, stop_event: threading.Event) -> None:
    """Write wall-clock epoch and UTC timestamps every second to detect OS/CPU freeze gaps."""
    with log_path.open("w", encoding="utf-8") as f:
        while not stop_event.is_set():
            now_utc = datetime.now(timezone.utc).isoformat(timespec="seconds")
            f.write(f"{time.time():.3f}\t{now_utc}\n")
            f.flush()
            stop_event.wait(1.0)


def analyze_heartbeat(log_path: Path) -> None:
    """Analyze heartbeat log for gaps > 3.0s indicating CPU freeze."""
    lines = [line.strip().split("\t") for line in log_path.read_text(encoding="utf-8").splitlines() if line.strip()]
    if len(lines) < 2:
        print("Not enough heartbeat samples recorded.")
        return

    print(f"\n=== Heartbeat Summary ({len(lines)} samples) ===")
    gaps = []
    for i in range(1, len(lines)):
        prev_ts, prev_utc = float(lines[i - 1][0]), lines[i - 1][1]
        curr_ts, curr_utc = float(lines[i][0]), lines[i][1]
        delta = curr_ts - prev_ts
        if delta > 3.0:
            gaps.append((delta, prev_utc, curr_utc))

    if not gaps:
        print("Result: NO CPU freeze detected (all intervals <= 3.0s). Background ran continuously.")
    else:
        for delta, start_u, end_u in gaps:
            print(f"Result: CPU FREEZE detected for {delta:.1f}s ({start_u} -> {end_u})")


def cmd_heartbeat(_args: argparse.Namespace) -> None:
    """Run a background heartbeat while user performs manual power-button, lid, or idle screen-off test."""
    with tempfile.TemporaryDirectory() as tmp:
        log_path = Path(tmp) / "heartbeat.log"
        stop_event = threading.Event()
        t = threading.Thread(target=heartbeat_worker, args=(log_path, stop_event), daemon=True)
        t.start()

        print(f"Heartbeat running (log: {log_path}).")
        print("Perform your manual action now (e.g., hold power button, close lid, or wait for screen-off).")
        print("After waking the machine, press ENTER here to inspect freeze gaps and Event Log...")
        try:
            input()
        except KeyboardInterrupt:
            pass
        finally:
            stop_event.set()
            t.join(timeout=2.0)

        analyze_heartbeat(log_path)
        print("Waiting 3s for Event Log flush...")
        time.sleep(3.0)
        print_recent_events(limit=8)


def cpu_burn_worker(stop_flag: multiprocessing.synchronize.Event) -> None:
    """Busy-loop one CPU core until stop_flag is set."""
    x = 0
    while not stop_flag.is_set():
        x = (x + 1) & 0xFFFFFFFF


def cmd_test_api_sleep(args: argparse.Namespace) -> None:
    """Call SetSuspendState(0, 0, 0) under busy CPU load (or --idle) and check resulting sleep/hibernate state."""
    stop_flag = multiprocessing.Event()
    burner = None
    if not args.idle:
        burner = multiprocessing.Process(target=cpu_burn_worker, args=(stop_flag,), daemon=True)
        burner.start()
        print("Started 1-core CPU busy loop. Waiting 2s before calling SetSuspendState(0, 0, 0)...")
        time.sleep(2.0)
    else:
        print("Idle mode selected. Calling SetSuspendState(0, 0, 0)...")

    try:
        if not enable_shutdown_privilege():
            print("Warning: failed to enable SeShutdownPrivilege; SetSuspendState may be refused.", file=sys.stderr)
        powrprof = ctypes.windll.powrprof
        ret = powrprof.SetSuspendState(False, False, False)
        print(f"SetSuspendState returned: {ret}")
    finally:
        stop_flag.set()
        if burner is not None:
            burner.join(timeout=2.0)

    print("Waiting 5s for Event Log flush after resume...")
    time.sleep(5.0)
    print_recent_events(limit=10)


def cmd_requests(_args: argparse.Namespace) -> None:
    """Show active power requests (requires admin for full list)."""
    _rc, stdout, stderr = run_cmd(["powercfg", "/requests"])
    out = stdout + ("\n" + stderr if stderr.strip() else "")
    print(out.strip())


def main() -> int:
    parser = argparse.ArgumentParser(description="Verify Windows Modern Standby, power button, and sleep API boundaries.")
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("check-s3", help="Run powercfg /a to verify S0 Low Power Idle vs S3 availability.")
    sub.add_parser("heartbeat", help="Record 1s heartbeat during manual power-button/lid/screen-off test.")

    api_p = sub.add_parser("test-api-sleep", help="Call SetSuspendState(0,0,0) and inspect Event Log for S0 vs S4.")
    api_p.add_argument("--idle", action="store_true", help="Run without CPU busy loop to test idle behavior.")

    sub.add_parser("requests", help="Run powercfg /requests to inspect sleep blockers.")

    args = parser.parse_args()
    handlers = {
        "check-s3": cmd_check_s3,
        "heartbeat": cmd_heartbeat,
        "test-api-sleep": cmd_test_api_sleep,
        "requests": cmd_requests,
    }
    handlers[args.command](args)
    return 0


if __name__ == "__main__":
    multiprocessing.freeze_support()
    sys.exit(main())
