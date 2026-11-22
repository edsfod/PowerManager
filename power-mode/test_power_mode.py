import unittest

from power_mode import FALLBACK_MIN, HOT_BATT_C, LOW_PCT, decide, profile


class Decide(unittest.TestCase):
    def d(self, mode='run', ac=False, pct=60, idle=0, batt=30.0, disp=False, cm=20):
        return decide(mode, ac, pct, idle, batt, disp, cm)

    def test_normal_never_acts(self):
        self.assertEqual(self.d(mode='normal', pct=1, idle=99999, batt=60), (None, None))

    def test_plugged_in_never_acts(self):
        self.assertEqual(self.d(ac=True, pct=1, idle=99999, batt=60), (None, None))

    def test_low_battery(self):
        self.assertIsNotNone(self.d(pct=LOW_PCT)[0])
        self.assertIsNone(self.d(pct=LOW_PCT + 1)[0])

    def test_hot_idle_hibernates_hot_active_warns(self):
        self.assertIsNotNone(self.d(batt=HOT_BATT_C, idle=120)[0])
        r, w = self.d(batt=HOT_BATT_C, idle=10)
        self.assertIsNone(r)
        self.assertIsNotNone(w)

    def test_unknown_temperature_is_ignored(self):
        self.assertEqual(self.d(batt=None, idle=60), (None, None))

    def test_fallback_after_missed_sleep(self):
        due = (20 + FALLBACK_MIN) * 60
        self.assertIsNone(self.d(idle=due - 1)[0])
        self.assertIsNotNone(self.d(idle=due)[0])
        self.assertIsNone(self.d(idle=due, disp=True)[0])   # video / presentation keeps the display on

    def test_profiles(self):
        r = profile('run', 25)
        self.assertEqual(r['ac']['sleep'], 0)
        self.assertEqual(r['dc']['sleep'], 25 * 60)
        self.assertEqual(r['ac']['lid'], 0)
        self.assertEqual(profile('normal', 25)['dc']['lid'], 1)


if __name__ == '__main__':
    unittest.main()
