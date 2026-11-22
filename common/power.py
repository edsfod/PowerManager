"""Windows power settings through powrprof.dll (no admin, no powercfg process).

- idle timeouts, lid and power-button actions of the active scheme (what `powercfg /qh` shows);
- the Settings -> Power Mode overlay for plugged in / on battery;
- hibernate (SetSuspendState), idle time (GetLastInputInfo), AC / battery percent.
"""
import ctypes
import ctypes.wintypes as wt
import uuid

powrprof = ctypes.WinDLL('powrprof')
k32 = ctypes.WinDLL('kernel32')
u32 = ctypes.WinDLL('user32')
advapi = ctypes.WinDLL('advapi32')


class GUID(ctypes.Structure):
    _fields_ = [('b', ctypes.c_ubyte * 16)]

    @classmethod
    def of(cls, s):
        g = cls()
        g.b[:] = uuid.UUID(s).bytes_le
        return g

    def __str__(self):
        return str(uuid.UUID(bytes_le=bytes(self.b)))


SUB_SLEEP = '238c9fa8-0aad-41ed-83f4-97be242c8f20'
SUB_VIDEO = '7516b95f-f776-4464-8c53-06167f40cc99'
SUB_BUTTONS = '4f971e89-eebd-4455-a8de-9e59040e7347'
# name -> (subgroup, setting). Timeouts in seconds (0 = never); actions 0 nothing, 1 sleep, 2 hibernate, 3 shut down.
SETTINGS = {
    'display': (SUB_VIDEO, '3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e'),   # VIDEOIDLE
    'sleep': (SUB_SLEEP, '29f6c1db-86da-48c5-9fdb-f2b67b1f44da'),     # STANDBYIDLE
    'lid': (SUB_BUTTONS, '5ca83367-6e45-459f-a27b-476b1d01c936'),     # LIDACTION
    'button': (SUB_BUTTONS, '7648efa3-dd9c-4e3e-b566-50f929386280'),  # PBUTTONACTION
}
POWER_MODES = {
    'efficiency': '961cc777-2547-4f9d-8174-7d86181b8a7a',
    'balanced': '00000000-0000-0000-0000-000000000000',
    'performance': 'ded574b5-45a0-4f42-8737-46345c09c238',
}
MODE_NAMES = {v: k for k, v in POWER_MODES.items()}


def _active_scheme():
    p = ctypes.POINTER(GUID)()
    if powrprof.PowerGetActiveScheme(None, ctypes.byref(p)):
        raise OSError('PowerGetActiveScheme failed')
    g = GUID()
    g.b[:] = p.contents.b
    k32.LocalFree(p)
    return g


def read_settings():
    """{'ac': {name: value}, 'dc': {...}} for SETTINGS in the active scheme."""
    s = _active_scheme()
    out = {'ac': {}, 'dc': {}}
    for name, (sub, key) in SETTINGS.items():
        for side, fn in (('ac', powrprof.PowerReadACValueIndex), ('dc', powrprof.PowerReadDCValueIndex)):
            v = wt.DWORD()
            if fn(None, ctypes.byref(s), ctypes.byref(GUID.of(sub)), ctypes.byref(GUID.of(key)), ctypes.byref(v)):
                raise OSError(f'PowerRead{side.upper()}ValueIndex {name} failed')
            out[side][name] = v.value
    return out


def write_settings(profile):
    """Write the values of `profile` ({'ac': {...}, 'dc': {...}}) that differ; returns the list changed."""
    cur = read_settings()
    s = _active_scheme()
    changed = []
    for side, fn in (('ac', powrprof.PowerWriteACValueIndex), ('dc', powrprof.PowerWriteDCValueIndex)):
        for name, val in profile.get(side, {}).items():
            if cur[side].get(name) == val:
                continue
            sub, key = SETTINGS[name]
            if fn(None, ctypes.byref(s), ctypes.byref(GUID.of(sub)), ctypes.byref(GUID.of(key)), wt.DWORD(int(val))):
                raise OSError(f'PowerWrite{side.upper()}ValueIndex {name}={val} failed')
            changed.append(f'{side}.{name}={val}')
    if changed:
        powrprof.PowerSetActiveScheme(None, ctypes.byref(s))
    return changed


def get_power_mode(ac):
    g = GUID()
    fn = powrprof.PowerGetUserConfiguredACPowerMode if ac else powrprof.PowerGetUserConfiguredDCPowerMode
    if fn(ctypes.byref(g)):
        return None
    return MODE_NAMES.get(str(g), str(g))


def set_power_mode(ac, name):
    fn = powrprof.PowerSetUserConfiguredACPowerMode if ac else powrprof.PowerSetUserConfiguredDCPowerMode
    if fn(ctypes.byref(GUID.of(POWER_MODES[name]))):
        raise OSError(f'PowerSetUserConfigured{"AC" if ac else "DC"}PowerMode {name} failed')


class _LUID(ctypes.Structure):
    _fields_ = [('Low', wt.DWORD), ('High', wt.LONG)]


class _TOKEN_PRIVILEGES(ctypes.Structure):
    _fields_ = [('Count', wt.DWORD), ('Luid', _LUID), ('Attr', wt.DWORD)]


advapi.OpenProcessToken.argtypes = [wt.HANDLE, wt.DWORD, ctypes.POINTER(wt.HANDLE)]
advapi.LookupPrivilegeValueW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p]
advapi.AdjustTokenPrivileges.argtypes = [wt.HANDLE, wt.BOOL, ctypes.c_void_p, wt.DWORD, ctypes.c_void_p, ctypes.c_void_p]
k32.GetCurrentProcess.restype = wt.HANDLE


def _enable_shutdown_privilege():
    tok = wt.HANDLE()
    if not advapi.OpenProcessToken(k32.GetCurrentProcess(), 0x0020 | 0x0008, ctypes.byref(tok)):
        return False
    tp = _TOKEN_PRIVILEGES(1, _LUID(), 0x2)
    ok = advapi.LookupPrivilegeValueW(None, 'SeShutdownPrivilege', ctypes.byref(tp.Luid)) and \
        advapi.AdjustTokenPrivileges(tok, False, ctypes.byref(tp), 0, None, None)
    k32.CloseHandle(tok)
    return bool(ok)


def hibernate():
    """Hibernate now (standard users hold SeShutdownPrivilege). Returns False if Windows refused."""
    _enable_shutdown_privilege()
    return bool(powrprof.SetSuspendState(True, False, False))


class _LASTINPUTINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]


def idle_seconds():
    """Seconds since the last keyboard / mouse / touch input in this session."""
    li = _LASTINPUTINFO(ctypes.sizeof(_LASTINPUTINFO), 0)
    if not u32.GetLastInputInfo(ctypes.byref(li)):
        return 0.0
    return ((k32.GetTickCount() - li.dwTime) & 0xFFFFFFFF) / 1000.0


class _SYSTEM_POWER_STATUS(ctypes.Structure):
    _fields_ = [('ACLineStatus', ctypes.c_ubyte), ('BatteryFlag', ctypes.c_ubyte),
                ('BatteryLifePercent', ctypes.c_ubyte), ('SystemStatusFlag', ctypes.c_ubyte),
                ('BatteryLifeTime', wt.DWORD), ('BatteryFullLifeTime', wt.DWORD)]


def os_power():
    """(ac_online: bool | None, percent: int | None) from GetSystemPowerStatus."""
    s = _SYSTEM_POWER_STATUS()
    if not k32.GetSystemPowerStatus(ctypes.byref(s)):
        return None, None
    ac = {1: True, 0: False}.get(s.ACLineStatus)
    pct = s.BatteryLifePercent if s.BatteryLifePercent <= 100 else None
    return ac, pct


class _PATH(ctypes.Structure):   # DISPLAYCONFIG_PATH_INFO, flattened (72 bytes)
    _fields_ = [('src', ctypes.c_byte * 20), ('t_adapter', ctypes.c_byte * 8), ('t_id', wt.UINT),
                ('t_mode', wt.UINT), ('tech', ctypes.c_uint32), ('rest', ctypes.c_byte * 28), ('flags', wt.UINT)]


_INTERNAL_TECH = {0x80000000, 6, 11, 13}   # internal, LVDS, embedded DisplayPort, embedded UDI


def external_display_active():
    """True if a display other than the laptop panel is active (QueryDisplayConfig, active paths)."""
    np_, nm = wt.UINT(), wt.UINT()
    if u32.GetDisplayConfigBufferSizes(2, ctypes.byref(np_), ctypes.byref(nm)):
        return False
    paths = (_PATH * max(1, np_.value))()
    modes = (ctypes.c_byte * (64 * max(1, nm.value)))()
    if u32.QueryDisplayConfig(2, ctypes.byref(np_), paths, ctypes.byref(nm), modes, None):
        return False
    return any(paths[i].tech not in _INTERNAL_TECH for i in range(np_.value))
