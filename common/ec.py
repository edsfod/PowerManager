"""Huawei MateBook EC through WMI (root\\wmi OemWMIMethod.OemWMIfun, admin only), via comtypes.

See ../notes/battery-wmi-decompile-20261111.md. Writes: only 03 10 (charge start/stop %).
Reads: 03 11 thresholds, 03 16 charge mode, 03 13 resume flag, 02 0D 00 charge current,
02 05 00 system power, 02 02 n temperature sensors, 02 0C 15 battery NTC.
Each EC command takes about 80 ms (the EC itself); connecting about 30 ms.
"""
import comtypes
import comtypes.client as cc


class EC:
    def __init__(self):
        comtypes.CoInitialize()
        loc = cc.CreateObject('WbemScripting.SWbemLocator', dynamic=True)
        self.svc = loc.ConnectServer('.', r'root\wmi')
        self._inst = None
        self._meth = None

    def call(self, head):
        """Send up to 64 bytes, return the first 8 output bytes."""
        if self._inst is None:
            self._inst = list(self.svc.InstancesOf('OemWMIMethod'))[0]
            self._meth = self.svc.Get('OemWMIMethod').Methods_.Item('OemWMIfun')
        ip = self._meth.InParameters.SpawnInstance_()
        ip.Properties_.Item('u8Input').Value = list(head) + [0] * (64 - len(head))
        out = self._inst.ExecMethod_('OemWMIfun', ip)
        return [int(x) for x in list(out.Properties_.Item('u8Output').Value)[:8]]

    def first(self, cls, props):
        """First instance of a root\\wmi class as {prop: value} (readable without admin)."""
        for o in self.svc.ExecQuery(f'SELECT {", ".join(props)} FROM {cls}'):
            return {p: o.Properties_.Item(p).Value for p in props}
        return {}


def _w(o):
    return int(o[1]) + 256 * int(o[2])


def battery_c(ec):
    b = ec.call([0x02, 0x0C, 0x15])
    return round(_w(b) / 10 - 273.15, 1) if b[0] == 0 and _w(b) else None


def read_all(ec, set_pair=None):
    """Optionally write the charge threshold (03 10), then read everything back.

    Returns (ec_info, win_info) in the shape battery-charge/var/state.json has always used.
    """
    set_status = None
    if set_pair:
        set_status = ec.call([0x03, 0x10, int(set_pair[0]), int(set_pair[1])])[0]
    temps = {}
    for n in range(16):
        o = ec.call([0x02, 0x02, n])
        if o[0] == 0:
            v = o[2] + o[3] / 10.0
            temps[str(n)] = round(-v if o[1] == 1 else v, 1)
    gbtt, gbcm, gbac = ec.call([0x03, 0x11]), ec.call([0x03, 0x16]), ec.call([0x03, 0x13])
    chg, sysp = ec.call([0x02, 0x0D, 0x00]), ec.call([0x02, 0x05, 0x00])
    info = {
        'hw_ok': gbtt[0] == 0 and gbtt[1] != 0xFF and gbtt[2] != 0xFF,
        'hw_start': gbtt[1],
        'hw_stop': gbtt[2],
        'charge_mode_raw': gbcm[:5],
        'resume_flag_raw': gbac[1],
        'charge_ma': _w(chg) if chg[0] == 0 else None,
        'system_w': sysp[1] if sysp[0] == 0 else None,
        'battery_c': battery_c(ec),
        'sensors_c': temps,
        'hottest_c': max(temps.values()) if temps else None,
    }
    if set_status is not None:
        info['set_status'] = set_status
    b = ec.first('BatteryStatus', ['PowerOnline', 'Charging', 'ChargeRate', 'DischargeRate', 'RemainingCapacity'])
    f = ec.first('BatteryFullChargedCapacity', ['FullChargedCapacity'])
    win = {'online': b.get('PowerOnline'), 'charging': b.get('Charging'), 'charge_mw': b.get('ChargeRate'),
           'discharge_mw': b.get('DischargeRate'), 'remaining_mwh': b.get('RemainingCapacity'),
           'full_mwh': f.get('FullChargedCapacity')}
    return info, win
