"""Tests for brain.Brain with a fake machine.  Run in this directory:
    D:\\miniforge3\\python.exe -m unittest test_brain
"""
import datetime as dt
import unittest

import brain as B

T0 = dt.datetime(2026, 11, 22, 8, 0, tzinfo=dt.timezone.utc)


class FakeIO:
    def __init__(self):
        self.profile = None
        self.timers = {}
        self.modes = {}
        self.calls = []
        self.idle = 600.0
        self.temp = None
        self.boost = {'active': False}
        self.saved = None
        self.external = False

    def log(self, m): pass
    def apply_profile(self, p): self.profile = p
    def set_power_mode(self, ac, name): self.modes[ac] = name
    def hibernate(self): self.calls.append('hibernate')
    def external_display(self): return self.external
    def idle_seconds(self): return self.idle
    def kick_ec(self, reason): self.calls.append(('ec', reason))
    def read_boost(self): return self.boost
    def battery_temp(self): return self.temp
    def save_mode(self, mode, until): self.saved = (mode, until)
    def arm(self, name, s): self.timers[name] = s
    def arm_at(self, name, t): self.timers[name] = t
    def cancel(self, name): self.timers.pop(name, None)
    def toast(self, title, lines): self.calls.append(('toast', title))
    def report_wake(self, t_off, t_on, notes, pct): self.calls.append(('wake', t_off, t_on, [n for _, n in notes], pct))


def at(s):
    return T0 + dt.timedelta(seconds=s)


class Base(unittest.TestCase):
    def make(self, mode='normal', until=None, ac=True, pct=70):
        io = FakeIO()
        b = B.Brain(io)
        b.start(T0, {'mode': mode, 'run_until': until}, True, True, ac, pct, {'active': False})
        return b, io


class Modes(Base):
    def test_start_normal_applies_profile_and_power_modes(self):
        b, io = self.make(pct=70)
        self.assertEqual(io.profile, B.PROFILES['normal'])
        self.assertEqual(io.modes, {True: 'performance', False: 'balanced'})

    def test_expired_run_mode_falls_back_to_normal(self):
        b, io = self.make('run', until=at(-1))
        self.assertEqual(b.mode, 'normal')

    def test_run_mode_duration_and_expiry(self):
        b, io = self.make()
        b.set_mode(T0, 'run', 4)
        self.assertEqual(io.profile, B.PROFILES['run'])
        self.assertEqual(io.timers['run_until'], at(4 * 3600))
        self.assertEqual(io.saved, ('run', at(4 * 3600)))
        b.on_timer(at(4 * 3600), 'run_until', 0)
        self.assertEqual(b.mode, 'normal')
        self.assertEqual(io.profile, B.PROFILES['normal'])
        self.assertIn(('toast', '保持运行到期'), io.calls)

    def test_bad_hours_use_default(self):
        b, io = self.make()
        b.set_mode(T0, 'run', 7)
        self.assertEqual(io.timers['run_until'], at(12 * 3600))

    def test_plugging_does_not_end_run_mode(self):  # R17
        b, io = self.make('run', until=at(3600))
        b.on_source(at(10), False)
        b.on_source(at(20), True)
        self.assertEqual(b.mode, 'run')

    def test_dc_power_mode_switches_at_50(self):  # R13
        b, io = self.make(ac=False, pct=51)
        b.on_percent(at(1), 50)
        self.assertEqual(io.modes[False], 'balanced')
        b.on_percent(at(2), 49)
        self.assertEqual(io.modes[False], 'efficiency')

    def test_ec_task_every_5_percent(self):  # R20
        b, io = self.make(ac=False, pct=70)
        for i, p in enumerate((69, 68, 67, 66, 65, 64)):
            b.on_percent(at(i), p)
        self.assertEqual([c for c in io.calls if c[0] == 'ec'], [('ec', '电量 69%'), ('ec', '电量 64%')])


class Lid(Base):
    def test_normal_lid_close_sleeps_after_a_minute(self):  # R9
        b, io = self.make()
        b.on_display(at(0), False)
        b.on_lid(at(0), False)
        self.assertEqual(io.timers['lid_grace'], B.LID_GRACE_S)
        b.on_timer(at(60), 'lid_grace', 0)
        self.assertEqual(io.profile, B.PROFILES['sleep_now'])
        self.assertEqual(b.sleep_reason, '合盖 1 分钟')

    def test_reopen_within_a_minute_does_nothing(self):
        b, io = self.make()
        b.on_lid(at(0), False)
        b.on_lid(at(30), True)
        self.assertNotIn('lid_grace', io.timers)
        self.assertEqual(io.profile, B.PROFILES['normal'])

    def test_external_monitor_keeps_it_awake(self):
        b, io = self.make()
        io.external = True
        b.on_lid(at(0), False)
        b.on_timer(at(60), 'lid_grace', 0)
        self.assertIsNone(b.sleep_reason)

    def test_open_lid_restores_settings(self):
        b, io = self.make()
        b.on_display(at(0), False)
        b.on_lid(at(0), False)
        b.on_timer(at(60), 'lid_grace', 0)
        b.on_lid(at(600), True)
        self.assertIsNone(b.sleep_reason)
        self.assertEqual(io.profile, B.PROFILES['normal'])

    def test_run_mode_lid_close_keeps_running(self):  # R2 R3
        b, io = self.make('run', until=at(3600))
        b.on_lid(at(0), False)
        self.assertNotIn('lid_grace', io.timers)

    def test_run_expiry_with_lid_closed_sleeps_a_minute_later(self):
        b, io = self.make('run', until=at(3600))
        b.on_display(at(0), False)
        b.on_lid(at(0), False)
        b.on_timer(at(3600), 'run_until', 0)
        self.assertEqual(io.timers['lid_grace'], B.LID_GRACE_S)


class Fallback(Base):
    def sleeping(self):
        b, io = self.make()
        b.on_display(at(0), False)
        b.on_lid(at(0), False)
        b.on_timer(at(60), 'lid_grace', 0)
        return b, io

    def test_hibernate_if_still_awake_after_two_minutes(self):  # R4 R11
        b, io = self.sleeping()
        b.on_timer(at(120), 'check', 0)
        self.assertNotIn('hibernate', io.calls)
        b.on_timer(at(180), 'check', 0)
        self.assertIn('hibernate', io.calls)

    def test_woke_in_bag_gets_another_go(self):  # R11
        b, io = self.sleeping()
        b.on_timer(at(5000), 'check', 4880)       # frozen for a long time: it slept, now awake
        self.assertNotIn('hibernate', io.calls)
        b.on_timer(at(5060), 'check', 0)
        b.on_timer(at(5120), 'check', 0)
        self.assertIn('hibernate', io.calls)

    def test_user_present_cancels(self):
        b, io = self.make()
        b.on_display(at(0), False)
        b.want_sleep(at(0), 'x')
        io.idle = 5
        b.on_timer(at(60), 'check', 0)
        self.assertIsNone(b.sleep_reason)


class Commute(Base):
    def commuting(self):
        b, io = self.make('run', until=at(12 * 3600))
        b.on_display(at(0), False)
        b.on_lid(at(0), False)
        b.on_source(at(0), False)
        return b, io

    def test_checks_temperature_every_three_minutes(self):
        b, io = self.commuting()
        self.assertEqual(io.timers['check'], B.COMMUTE_CHECK_S)
        b.on_timer(at(180), 'check', 0)
        self.assertIn(('ec', '通勤查温度'), io.calls)
        io.temp = 38.0
        b.on_timer(at(190), 'ec_done', 0)
        self.assertIsNone(b.sleep_reason)
        io.temp = 40.2
        b.on_timer(at(370), 'check', 0)
        b.on_timer(at(380), 'ec_done', 0)
        self.assertEqual(b.sleep_reason, '电池 40 °C，提前睡')

    def test_hot_but_in_use_does_not_sleep(self):
        b, io = self.make('run', until=at(12 * 3600))
        b.on_source(at(0), False)
        io.idle, io.temp = 10, 45.0
        b.on_timer(at(190), 'ec_done', 0)
        self.assertIsNone(b.sleep_reason)

    def test_plug_in_stops_checks(self):
        b, io = self.commuting()
        b.on_source(at(100), True)
        self.assertNotIn('check', io.timers)

    def test_windows_failed_to_sleep_after_30_min(self):
        b, io = self.commuting()
        io.idle = 33 * 60
        b.on_timer(at(33 * 60), 'check', 0)
        self.assertIsNotNone(b.sleep_reason)

    def test_woke_in_bag_after_sleeping(self):
        b, io = self.commuting()
        b.on_timer(at(9000), 'check', 8000)
        self.assertEqual(b.sleep_reason, '通勤途中在包里醒了，让它再睡')


class Wake(Base):
    def test_report_after_a_minute_off(self):  # R12
        b, io = self.make(pct=80)
        b.on_display(at(0), False)
        b.on_lid(at(1), False)
        b.on_timer(at(61), 'lid_grace', 0)
        b.on_percent(at(500), 78)
        b.on_lid(at(600), True)
        b.on_display(at(600), True)
        w = [c for c in io.calls if c[0] == 'wake'][0]
        self.assertEqual(w[3], ['合盖 1 分钟'])
        self.assertEqual(w[4], (80, 78))

    def test_report_when_lid_closed_but_display_never_reported_off(self):
        b, io = self.make(pct=80)
        b.on_lid(at(0), False)
        b.on_timer(at(60), 'lid_grace', 0)
        b.on_lid(at(300), True)
        w = [c for c in io.calls if c[0] == 'wake'][0]
        self.assertEqual((w[1], w[2]), (T0, at(300)))
        self.assertEqual(io.profile, B.PROFILES['normal'])

    def test_no_report_while_lid_still_closed(self):
        b, io = self.make()
        b.on_lid(at(0), False)
        b.on_display(at(10), False)
        b.on_display(at(300), True)      # e.g. external monitor woke
        self.assertFalse([c for c in io.calls if c[0] == 'wake'])

    def test_no_report_for_short_screen_off(self):
        b, io = self.make()
        b.on_display(at(0), False)
        b.on_display(at(20), True)
        self.assertFalse([c for c in io.calls if c[0] == 'wake'])


class Boost(Base):
    def test_boost_timers(self):
        b, io = self.make()
        b.on_boost(T0, {'active': True, 'target': 95, 'deadline': at(4 * 3600), 'full_since': at(100),
                        'hold_min': 60, 'reached': True, 'unplugged_since': None})
        self.assertEqual(io.timers['boost_deadline'], at(4 * 3600))
        self.assertEqual(io.timers['boost_hold'], at(100 + 3600))
        b.on_timer(at(3700), 'boost_hold', 0)
        self.assertIn(('ec', '冲高保持满'), io.calls)

    def test_unplug_before_target_arms_ten_minutes(self):
        b, io = self.make()
        b.on_boost(T0, {'active': True, 'target': 95, 'deadline': at(4 * 3600), 'full_since': None,
                        'hold_min': 60, 'reached': False, 'unplugged_since': None})
        b.on_source(at(50), False)
        self.assertEqual(io.timers['boost_unplug'], at(50 + B.BOOST_UNPLUG_S))
        b.on_source(at(100), True)
        self.assertNotIn('boost_unplug', io.timers)

    def test_boost_ended(self):
        b, io = self.make()
        b.on_boost(T0, {'active': True, 'target': 95, 'deadline': at(10), 'full_since': None, 'hold_min': 60,
                        'reached': False, 'unplugged_since': None})
        b.on_boost(at(20), {'active': False})
        self.assertNotIn('boost_deadline', io.timers)


if __name__ == '__main__':
    unittest.main()
