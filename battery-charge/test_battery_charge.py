"""Tests for the boost / revert policy of battery_charge.py (no EC access).

Run: python -m unittest test_battery_charge  (from this directory)
"""
import datetime as dt
import unittest

import battery_charge as bc

T0 = dt.datetime(2026, 11, 11, 8, 0, tzinfo=dt.timezone.utc)
REQ = {'target_stop': 95, 'full_hold_minutes': 60, 'max_total_hours': 4.0}


def at(minutes):
    return T0 + dt.timedelta(minutes=minutes)


class Policy(unittest.TestCase):
    def test_daily_without_boost(self):
        boost, target, event = bc.decide(None, None, T0, True, 50)
        self.assertEqual(target, (75, 80))
        self.assertIsNone(event)

    def test_request_starts_boost(self):
        boost, target, event = bc.decide(None, REQ, T0, True, 60)
        self.assertTrue(boost['active'])
        self.assertEqual(target, (90, 95))
        self.assertIn('Boost started', event)

    def test_request_is_clamped(self):
        boost, target, _ = bc.decide(None, {'target_stop': 120, 'full_hold_minutes': 1,
                                            'max_total_hours': 99}, T0, True, 60)
        self.assertEqual(target, (95, 100))
        self.assertEqual(boost['full_hold_minutes'], 5)
        self.assertEqual(boost['max_total_hours'], 24.0)

    def test_hold_on_ac_reverts_after_hold_minutes(self):
        boost, _, _ = bc.decide(None, REQ, T0, True, 60)
        boost, target, _ = bc.decide(boost, None, at(30), True, 95)   # reaches target
        self.assertEqual(target, (90, 95))
        boost, target, _ = bc.decide(boost, None, at(89), True, 95)   # 59 min held
        self.assertEqual(target, (90, 95))
        boost, target, event = bc.decide(boost, None, at(90), True, 95)
        self.assertEqual(target, (75, 80))
        self.assertIn('Held', event)
        self.assertFalse(boost['active'])

    def test_dropping_below_target_resets_hold(self):
        boost, _, _ = bc.decide(None, REQ, T0, True, 60)
        boost, _, _ = bc.decide(boost, None, at(10), True, 95)
        boost, _, _ = bc.decide(boost, None, at(40), True, 93)        # resets
        boost, target, _ = bc.decide(boost, None, at(75), True, 95)   # new hold starts
        boost, target, _ = bc.decide(boost, None, at(100), True, 95)  # only 25 min
        self.assertEqual(target, (90, 95))

    def test_unplug_after_reaching_target_reverts_at_once(self):
        boost, _, _ = bc.decide(None, REQ, T0, True, 60)
        boost, _, _ = bc.decide(boost, None, at(30), True, 95)
        boost, target, event = bc.decide(boost, None, at(35), False, 94)
        self.assertEqual(target, (75, 80))
        self.assertIn('Unplugged', event)

    def test_unplug_before_target_needs_ten_minutes(self):
        boost, _, _ = bc.decide(None, REQ, T0, True, 60)
        boost, target, _ = bc.decide(boost, None, at(5), False, 60)
        self.assertEqual(target, (90, 95))
        boost, target, _ = bc.decide(boost, None, at(10), True, 60)   # plugged back: timer resets
        boost, target, _ = bc.decide(boost, None, at(15), False, 60)
        boost, target, _ = bc.decide(boost, None, at(24), False, 59)  # 9 minutes
        self.assertEqual(target, (90, 95))
        boost, target, event = bc.decide(boost, None, at(25), False, 59)
        self.assertEqual(target, (75, 80))
        self.assertIn('10 minutes', event)

    def test_boost_started_on_battery_counts_from_start(self):
        boost, _, _ = bc.decide(None, REQ, T0, False, 60)
        boost, target, _ = bc.decide(boost, None, at(10), False, 58)
        self.assertEqual(target, (75, 80))

    def test_deadline_reverts(self):
        boost, _, _ = bc.decide(None, REQ, T0, True, 60)
        boost, target, _ = bc.decide(boost, None, at(239), True, 70)
        self.assertEqual(target, (90, 95))
        boost, target, event = bc.decide(boost, None, at(240), True, 70)
        self.assertEqual(target, (75, 80))
        self.assertIn('Max boost duration', event)

    def test_inactive_boost_stays_daily(self):
        boost = {'active': False, 'last_reason': 'x'}
        boost, target, event = bc.decide(boost, None, T0, True, 90)
        self.assertEqual(target, (75, 80))
        self.assertIsNone(event)


if __name__ == '__main__':
    unittest.main()
