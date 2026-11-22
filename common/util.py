"""Paths, UTC time, JSON files and logs shared by agent/, battery-charge/ and ui/."""
import datetime as dt
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
AGENT_VAR = ROOT / 'agent' / 'var'
BATTERY_VAR = ROOT / 'battery-charge' / 'var'
BATTERY_STATE = BATTERY_VAR / 'state.json'
BATTERY_KICK = BATTERY_VAR / 'kick.json'      # why the agent started the EC task (consumed by it)
BATTERY_TASK = 'Battery-ChargeLimit'
AGENT_TASK = 'PowerManager-Agent'
AGENT_WINDOW_CLASS = 'PowerManagerAgent'
AGENT_RELOAD_MSG = 'PowerManager.Reload'      # RegisterWindowMessage name, posted by the UI
AUMID = 'PowerManager'                        # toast sender, registered by install.ps1
ICON_PNG = ROOT / 'ui' / 'icon.png'
PYTHONW = r'D:\miniforge3\pythonw.exe'
POWERSHELL = r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
NO_WINDOW = 0x08000000


def now_utc():
    return dt.datetime.now(dt.timezone.utc)


def iso(t):
    return t.strftime('%Y-%m-%dT%H:%M:%SZ')


def parse_iso(s):
    return dt.datetime.fromisoformat(s.replace('Z', '+00:00'))


def load_json(path, default=None):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8'))
    except Exception:
        return default


def save_json(path, obj):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + '.tmp')
    tmp.write_text(json.dumps(obj, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')
    os.replace(tmp, path)


def append_line(path, line, max_bytes=None):
    """Append one line; when the file grows past max_bytes it is rotated to <name>.1 first."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    if max_bytes and path.exists() and path.stat().st_size > max_bytes:
        os.replace(path, path.with_name(path.name + '.1'))
    with open(path, 'a', encoding='utf-8', newline='\n') as f:
        f.write(line + '\n')
