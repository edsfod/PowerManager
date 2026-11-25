"""Verify PowerManager UI layout invariants (intrinsic height + zero overflow on constrained viewports)."""

from __future__ import annotations

import re
import shutil
import subprocess
import sys
from pathlib import Path

EDGE_CANDIDATES = [
    Path(r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
    Path(r"C:\Program Files\Microsoft\Edge\Application\msedge.exe"),
]

# (name, width, height, query, max_allowed_main_height_or_None_for_viewport_check)
CASES: list[tuple[str, int, int, str, int | None]] = [
    ("compact-normal", 720, 960, "preset=normal&hideMockBar=1&audit=1", 730),
    ("compact-draft", 720, 960, "preset=normal&draft=1&hideMockBar=1&audit=1", 810),
    ("expanded-draft", 960, 960, "preset=normal&draft=1&expanded=1&hideMockBar=1&audit=1", 810),
    ("max-1280x720-run", 1280, 720, "preset=run&expanded=1&maximized=1&hideMockBar=1&audit=1", None),
    ("max-1440x912-normal", 1440, 912, "preset=normal&expanded=1&maximized=1&hideMockBar=1&audit=1", None),
    ("max-1440x912-run", 1440, 912, "preset=run&expanded=1&maximized=1&hideMockBar=1&audit=1", None),
    ("max-1920x1040-run", 1920, 1040, "preset=run&expanded=1&maximized=1&hideMockBar=1&audit=1", None),
    ("max-1920x1040-draft", 1920, 1040, "preset=normal&draft=1&expanded=1&maximized=1&hideMockBar=1&audit=1", None),
]


def find_edge() -> Path:
    for p in EDGE_CANDIDATES:
        if p.exists():
            return p
    found = shutil.which("msedge")
    if found:
        return Path(found)
    raise FileNotFoundError("Microsoft Edge (msedge.exe) not found.")


def check_case(
    edge: Path, index_uri: str, name: str, width: int, height: int, query: str, max_main_h: int | None
) -> tuple[bool, str]:
    url = f"{index_uri}?{query}"
    cmd = [
        str(edge),
        "--headless=new",
        "--disable-gpu",
        "--no-first-run",
        f"--window-size={width},{height}",
        "--virtual-time-budget=2000",
        "--dump-dom",
        url,
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
    if proc.returncode != 0:
        return False, f"{name} ({width}x{height}): Edge exited {proc.returncode}"

    dom = proc.stdout
    m_sh = re.search(r'data-scroll-height="(\d+)"', dom)
    m_ch = re.search(r'data-client-height="(\d+)"', dom)
    m_mh = re.search(r'data-main-height="(\d+)"', dom)
    if not m_sh or not m_ch or not m_mh:
        return False, f"{name} ({width}x{height}): missing layout audit attributes"

    sh, ch, mh = int(m_sh.group(1)), int(m_ch.group(1)), int(m_mh.group(1))
    if max_main_h is not None:
        ok = mh > 0 and mh <= max_main_h
        status = "OK" if ok else "TALL"
        return ok, f"[{status}] {name:20s} width={width}  intrinsicMainHeight={mh} <= {max_main_h}"

    ok = sh <= ch
    status = "OK" if ok else "OVERFLOW"
    return ok, f"[{status}] {name:20s} viewport={width}x{height}  scrollHeight={sh} <= clientHeight={ch} (main={mh})"


def main() -> int:
    root = Path(__file__).resolve().parent.parent
    index_path = root / "src" / "PowerManager.UI" / "web" / "index.html"
    if not index_path.exists():
        print(f"ERROR: {index_path} not found", file=sys.stderr)
        return 1

    edge = find_edge()
    index_uri = index_path.as_uri()
    failed = 0
    for name, w, h, query, max_main_h in CASES:
        ok, line = check_case(edge, index_uri, name, w, h, query, max_main_h)
        print(line)
        if not ok:
            failed += 1

    if failed:
        print(f"FAILED: {failed}/{len(CASES)} UI layout checks failed.", file=sys.stderr)
        return 1

    print(f"OK: All {len(CASES)}/{len(CASES)} UI layout checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
