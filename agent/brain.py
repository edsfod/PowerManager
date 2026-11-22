"""Decisions of the resident agent, without Windows calls (those go through `io`, see agent.py).

Every method is an event: the agent calls it when Windows tells it something (lid, display, power
source, battery %), when one of its one-shot timers is due, or when the UI changed the mode.
Times are aware UTC datetimes; `late` is how many seconds after its due time a timer ran. A timer
that runs much later than due means the process was frozen, i.e. the machine was asleep.

Requirements are R1-R20 in ../REQUIREMENTS.md; the design is ../DESIGN.md.
"""
import datetime as dt

MIN, HOUR = 60, 3600

PROFILES = {   # Windows settings per mode (seconds; 0 = never; lid/button 0 = do nothing)
    'normal': {'ac': {'display': 15 * MIN, 'sleep': 1 * HOUR, 'lid': 0, 'button': 0},     # R8 R9 R10
               'dc': {'display': 5 * MIN, 'sleep': 15 * MIN, 'lid': 0, 'button': 0}},
    'run': {'ac': {'display': 15 * MIN, 'sleep': 0, 'lid': 0, 'button': 0},               # R2 R7
            'dc': {'display': 5 * MIN, 'sleep': 30 * MIN, 'lid': 0, 'button': 0}},        # R3
    # "sleep now": Windows sleeps on its own a few seconds after the last input (tested 2026-11-22)
    'sleep_now': {'ac': {'display': 10, 'sleep': 15, 'lid': 0, 'button': 0},
                  'dc': {'display': 10, 'sleep': 15, 'lid': 0, 'button': 0}},
}
RUN_HOURS = (2, 4, 8, 12, 24)
DEFAULT_RUN_HOURS = 12
LID_GRACE_S = 1 * MIN            # R9
SLEEP_CHECK_S = 1 * MIN          # while we want it asleep: look every minute (only runs if it is awake)
SLEEP_GIVE_UP_S = 2 * MIN        # still awake this long after asking -> hibernate (R4 R11)
COMMUTE_CHECK_S = 3 * MIN        # run mode on battery: battery temperature every 3 minutes
COMMUTE_IDLE_S = 30 * MIN        # R3 (Windows' own timeout; +2 min margin before we step in)
HOT_C = 40.0                     # R3
TEMP_READ_DELAY_S = 10           # after starting the EC task, read its result this much later
FROZEN_S = 30                    # a timer this late means the process was frozen (asleep)
BOOST_UNPLUG_S = 10 * MIN        # boost: unplugged this long before reaching the target -> revert
PCT_STEP = 5                     # R20
HALF = 50                        # R13


def dc_power_mode(pct):
    return 'balanced' if pct >= HALF else 'efficiency'


class Brain:
    def __init__(self, io):
        self.io = io
        self.mode = 'normal'
        self.run_until = None
        self.lid_open = True
        self.display_on = True
        self.ac = True
        self.pct = None
        self.sleep_reason = None       # set while we want the machine asleep
        self.sleep_asked_at = None
        self.awake_since = None
        self.away_since = None         # lid closed or display off since then (for the wake notification)
        self.pct_at_off = None
        self.pct_logged = None         # last 5% step the EC task was started for
        self.notes = []                # [(time, text)] for the wake notification
        self.boost = None              # copy of battery-charge state.json "boost"

    # ------------------------------------------------------------ helpers

    def note(self, now, text):
        self.notes.append((now, text))
        self.notes = [(t, s) for t, s in self.notes if (now - t).total_seconds() < 2 * 86400]
        self.io.log(text)

    def profile(self):
        return PROFILES['sleep_now'] if self.sleep_reason else PROFILES[self.mode]

    def apply(self):
        self.io.apply_profile(self.profile())

    def commuting(self):
        return self.mode == 'run' and not self.ac

    def want_sleep(self, now, reason):
        if self.sleep_reason:
            return
        self.sleep_reason = reason
        self.sleep_asked_at = now
        self.note(now, reason)
        self.apply()
        self.io.arm('check', SLEEP_CHECK_S)

    def stop_sleep(self, now, why):
        if not self.sleep_reason:
            return
        self.io.log(f'stop asking for sleep ({why})')
        self.sleep_reason = None
        self.sleep_asked_at = None
        self.apply()
        self.arm_check()

    def arm_check(self):
        if self.sleep_reason:
            self.io.arm('check', SLEEP_CHECK_S)
        elif self.commuting():
            self.io.arm('check', COMMUTE_CHECK_S)
        else:
            self.io.cancel('check')

    def set_power_modes(self):
        self.io.set_power_mode(True, 'performance')                         # R18
        if self.pct is not None:
            self.io.set_power_mode(False, dc_power_mode(self.pct))         # R13

    def sync_boost(self, now):
        """Arm the boost timers from battery-charge's state (the EC task decides; we only wake it)."""
        b = self.boost or {}
        if not b.get('active'):
            for n in ('boost_deadline', 'boost_hold', 'boost_unplug'):
                self.io.cancel(n)
            return
        self.io.arm_at('boost_deadline', b['deadline'])
        if b.get('full_since'):
            self.io.arm_at('boost_hold', b['full_since'] + dt.timedelta(minutes=b['hold_min']))
        else:
            self.io.cancel('boost_hold')
        if not self.ac and not b.get('reached'):
            self.io.arm_at('boost_unplug', (b.get('unplugged_since') or now) + dt.timedelta(seconds=BOOST_UNPLUG_S))
        else:
            self.io.cancel('boost_unplug')

    def kick_ec(self, now, reason):
        self.io.kick_ec(reason)
        self.io.arm('ec_done', TEMP_READ_DELAY_S)

    # ------------------------------------------------------------ events

    def start(self, now, saved, lid_open, display_on, ac, pct, boost):
        """Agent (re)started: restore the mode, apply settings."""
        self.mode = saved.get('mode', 'normal')
        self.run_until = saved.get('run_until')
        if self.mode == 'run' and (not self.run_until or self.run_until <= now):
            self.mode, self.run_until = 'normal', None
        self.lid_open, self.display_on, self.ac, self.pct = lid_open, display_on, ac, pct
        self.boost = boost
        self.pct_logged = pct // PCT_STEP if pct is not None else None
        self.awake_since = now
        self.apply()
        self.set_power_modes()
        if self.run_until:
            self.io.arm_at('run_until', self.run_until)
        if not self.lid_open and self.mode == 'normal':
            self.io.arm('lid_grace', LID_GRACE_S)
        self.arm_check()
        self.sync_boost(now)
        self.io.log(f'start: mode={self.mode} until={self.run_until} lid={"open" if lid_open else "closed"} '
                    f'{"AC" if ac else "DC"} {pct}%')

    def set_mode(self, now, mode, hours=None):
        """From the UI (R1 R17)."""
        if mode == 'run':
            hours = hours if hours in RUN_HOURS else DEFAULT_RUN_HOURS
            self.mode, self.run_until = 'run', now + dt.timedelta(hours=hours)
            self.io.arm_at('run_until', self.run_until)
            self.io.cancel('lid_grace')
            self.note(now, f'切到保持运行 {hours} 小时')
        else:
            self.mode, self.run_until = 'normal', None
            self.io.cancel('run_until')
            self.note(now, '切回普通模式')
            if not self.lid_open:
                self.io.arm('lid_grace', LID_GRACE_S)
        self.io.save_mode(self.mode, self.run_until)
        if self.sleep_reason and self.mode == 'run' and self.ac:
            self.stop_sleep(now, 'switched to run on AC')
        self.apply()
        self.arm_check()

    def went_away(self, now):
        if self.away_since is None:
            self.away_since = now
            self.pct_at_off = self.pct

    def came_back(self, now):
        """Lid open and display on again: report what happened if it was away a minute or more (R12)."""
        if not (self.lid_open and self.display_on) or self.away_since is None:
            return
        t_off, self.away_since = self.away_since, None
        self.awake_since = now
        self.set_power_modes()
        if (now - t_off).total_seconds() >= 60:
            self.io.report_wake(t_off, now, list(self.notes), (self.pct_at_off, self.pct))
            self.notes = [(t, s) for t, s in self.notes if t > now]

    def on_lid(self, now, is_open):
        if is_open == self.lid_open:
            return
        self.lid_open = is_open
        if is_open:
            self.io.cancel('lid_grace')
            self.stop_sleep(now, 'lid opened')
            self.came_back(now)
        else:
            self.went_away(now)
            if self.mode == 'normal':
                self.io.arm('lid_grace', LID_GRACE_S)

    def on_display(self, now, on):
        if on == self.display_on:
            return
        self.display_on = on
        if not on:
            self.went_away(now)
            return
        if self.lid_open and self.sleep_reason:
            self.stop_sleep(now, 'display on, lid open')
        self.came_back(now)

    def on_source(self, now, ac):
        if ac == self.ac:
            return
        self.ac = ac
        if ac and self.sleep_reason and self.mode == 'run':
            self.stop_sleep(now, 'plugged in, run mode')
        self.set_power_modes()
        self.arm_check()
        if self.boost and self.boost.get('active'):
            if not ac and not self.boost.get('reached'):
                self.boost['unplugged_since'] = now
            self.sync_boost(now)
        # the EC task has its own trigger for power source changes

    def on_percent(self, now, pct):
        if pct == self.pct:
            return
        self.pct = pct
        self.io.set_power_mode(False, dc_power_mode(pct))                 # R13 (only changes at 50%)
        step = pct // PCT_STEP
        b = self.boost or {}
        if step != self.pct_logged:                                        # R20
            self.pct_logged = step
            self.kick_ec(now, f'电量 {pct}%')
        elif b.get('active') and self.ac and pct >= b.get('target', 101) and not b.get('full_since'):
            self.kick_ec(now, f'冲高到 {pct}%')

    def on_boost(self, now, boost):
        """battery-charge's state changed (after an EC task run, or the UI started a boost)."""
        self.boost = boost
        self.sync_boost(now)

    def on_timer(self, now, name, late):
        frozen = late > FROZEN_S
        if frozen:
            self.awake_since = now
        if name == 'lid_grace':
            if self.lid_open or self.mode != 'normal':
                return
            if self.io.external_display():   # lid closed on an external monitor: keep working
                self.io.log('lid closed but an external display is active: not sleeping')
                return
            self.want_sleep(now, '合盖 1 分钟')
        elif name == 'run_until':
            self.mode, self.run_until = 'normal', None
            self.io.save_mode('normal', None)
            self.note(now, '保持运行到期，回到普通模式')
            self.apply()
            self.arm_check()
            self.io.toast('保持运行到期', ['已回到普通模式：插电闲置 1 小时睡，用电池 15 分钟睡，合盖 1 分钟睡'])
            if not self.lid_open:
                self.io.arm('lid_grace', LID_GRACE_S)
        elif name == 'check':
            self.on_check(now, frozen)
        elif name == 'ec_done':
            self.on_boost(now, self.io.read_boost())
            if self.commuting() and not self.sleep_reason:
                self.on_temperature(now, self.io.battery_temp())
        elif name in ('boost_deadline', 'boost_hold', 'boost_unplug'):
            self.kick_ec(now, {'boost_deadline': '冲高到期', 'boost_hold': '冲高保持满',
                               'boost_unplug': '冲高中拔电 10 分钟'}[name])

    def on_check(self, now, frozen):
        """Runs only while the machine is awake: either we want it asleep, or it is commuting."""
        idle = self.io.idle_seconds()
        if self.sleep_reason:
            if self.lid_open and idle < 60:
                self.stop_sleep(now, 'someone is using it')
                return
            if frozen:      # it slept and has just woken again (e.g. in the bag): give it another go (R11)
                self.sleep_asked_at = now
                self.io.log('woke while we want it asleep; waiting for it to sleep again')
            elif (now - self.sleep_asked_at).total_seconds() >= SLEEP_GIVE_UP_S:
                self.note(now, f'{self.sleep_reason}，但 {SLEEP_GIVE_UP_S // 60} 分钟没睡下去，改为休眠')
                self.io.hibernate()
                self.sleep_asked_at = now      # if hibernate failed, try again next round
            self.io.arm('check', SLEEP_CHECK_S)
            return
        if not self.commuting():
            return
        if frozen and not self.lid_open:
            self.want_sleep(now, '通勤途中在包里醒了，让它再睡')
            return
        if idle >= COMMUTE_IDLE_S + 2 * MIN:
            self.want_sleep(now, f'用电池 {COMMUTE_IDLE_S // 60} 分钟没人操作，Windows 没睡下去')
            return
        self.kick_ec(now, '通勤查温度')
        self.io.arm('check', COMMUTE_CHECK_S)

    def on_temperature(self, now, temp):
        if temp is None or temp < HOT_C:
            return
        if self.lid_open and self.io.idle_seconds() < 5 * MIN:
            self.io.log(f'battery {temp} C but someone is using it: not sleeping')
            return
        self.want_sleep(now, f'电池 {temp:.0f} °C，提前睡')
