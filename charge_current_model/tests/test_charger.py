"""Unit + closed‑loop tests.  Run:  python -m unittest discover charge_current_model/tests
(also pytest‑compatible)."""
import unittest

from charge_current_model import (
    BatteryState, ChargerConfig, ChargeController, DampingConfig, TempProfile,
    compute_adaptive_charge_current, compute_charge_decision,
)
from charge_current_model import charger as ch
from charge_current_model.sim import simulate

NAN = float("nan")


def st(**kw):
    base = dict(soc=0.5, voltage=3.80, temp_c=25.0, soh=1.0, r_internal=0.05, i_prev=2.0)
    base.update(kw)
    return BatteryState(**base)


class TestValidation(unittest.TestCase):
    def test_nan_inputs_raise(self):
        cfg = ChargerConfig()
        for f in ("soc", "voltage", "temp_c", "soh", "r_internal", "i_prev"):
            with self.subTest(field=f), self.assertRaises(ValueError):
                compute_charge_decision(st(**{f: NAN}), cfg)

    def test_nonpositive_resistance_raises(self):
        for r in (0.0, -0.01):
            with self.assertRaises(ValueError):
                compute_charge_decision(st(r_internal=r), ChargerConfig())

    def test_bad_config_raises(self):
        with self.assertRaises(ValueError):
            ChargerConfig(c_rate_cutoff=2.0)
        with self.assertRaises(ValueError):
            ChargerConfig(capacity_ah=0)
        with self.assertRaises(ValueError):
            TempProfile(t_cold_end=50.0)
        with self.assertRaises(ValueError):
            ChargerConfig(temp=TempProfile(c_rate_cold=2.0), c_rate_max=1.5)
        with self.assertRaises(ValueError):
            ChargerConfig(v_max_new=NAN)
        with self.assertRaises(ValueError):
            TempProfile(t_min_charge=NAN)
        with self.assertRaises(ValueError):
            DampingConfig(cv_approach_gain=NAN)
        with self.assertRaises(TypeError):
            ChargerConfig(temp="invalid")  # type: ignore[arg-type]

    def test_controller_latches_fault_on_nan_temperature(self):
        ctl = ChargeController(ChargerConfig())
        self.assertEqual(ctl.step(st(temp_c=NAN)).current_a, 0.0)
        self.assertIsNotNone(ctl.fault)
        # stays latched even with valid data
        d = ctl.step(st())
        self.assertEqual((d.current_a, d.reason), (0.0, ch.R_FAULT_LATCHED))
        ctl.reset()
        self.assertGreater(ctl.step(st()).current_a, 0.0)


class TestTemperature(unittest.TestCase):
    def setUp(self):
        self.cfg = ChargerConfig()

    def c_max(self, t):
        return ch._temp_c_rate_limit(st(temp_c=t), self.cfg)

    def test_windows(self):
        self.assertEqual(self.c_max(0.0), 0.2)
        self.assertEqual(self.c_max(9.9), 0.2)
        self.assertAlmostEqual(self.c_max(15.0), 0.85)
        self.assertEqual(self.c_max(25.0), 1.5)
        self.assertAlmostEqual(self.c_max(45.0), 0.45)

    def test_monotonic_cold_side(self):
        vals = [self.c_max(t / 2) for t in range(0, 41)]
        self.assertEqual(vals, sorted(vals))

    def test_out_of_window_stops(self):
        for t in (-0.1, 45.1):
            self.assertEqual(compute_charge_decision(st(temp_c=t), self.cfg).reason, ch.R_TEMP)

    def test_hot_voltage_derate(self):
        self.assertAlmostEqual(ch._limit_voltage(st(temp_c=45.0), self.cfg), 4.15)


class TestCurrentLimits(unittest.TestCase):
    def setUp(self):
        self.cfg = ChargerConfig()

    def test_cv_limit_is_strict(self):
        # Over the limit by 5 mV → current must be cut so predicted V <= limit
        s = st(soc=0.6, voltage=4.205, i_prev=1.0)
        d = compute_charge_decision(s, self.cfg)
        ocv = s.voltage - s.i_prev * s.r_internal
        self.assertLessEqual(ocv + d.current_a * s.r_internal, d.v_limit + 1e-9)
        self.assertEqual(d.reason, ch.R_CV)

    def test_decrease_is_immediate(self):
        s = st(soc=0.6, voltage=4.25, i_prev=3.0)  # 50 mV over
        d = compute_charge_decision(s, self.cfg)
        self.assertAlmostEqual(d.current_a, (4.20 - (4.25 - 0.15)) / 0.05)

    def test_ramp_up_limited(self):
        d = compute_charge_decision(st(soc=0.3, voltage=3.70, i_prev=0.8), self.cfg)
        self.assertAlmostEqual(d.current_a, 0.8 + 0.25 * 4.5)
        self.assertEqual(d.reason, ch.R_RAMP)

    def test_gross_overvoltage_stops(self):
        d = compute_charge_decision(st(voltage=4.40, i_prev=0.0), self.cfg)
        self.assertEqual(d.reason, ch.R_OVERVOLTAGE)

    def test_precharge(self):
        d = compute_charge_decision(st(soc=0.0, voltage=2.8, i_prev=0.0), self.cfg)
        self.assertAlmostEqual(d.current_a, 0.1 * 4.5)
        self.assertEqual(d.reason, ch.R_PRECHARGE)

    def test_taper_ends_at_cutoff(self):
        c, _ = ch._base_c_rate(st(soc=0.99999), 4.0, 1.5, self.cfg)
        self.assertAlmostEqual(c, self.cfg.c_rate_cutoff, places=3)


class TestTermination(unittest.TestCase):
    def test_stateless_terminates(self):
        s = st(soc=0.99, voltage=4.199, i_prev=0.1)
        self.assertEqual(compute_adaptive_charge_current(s, ChargerConfig()), 0.0)

    def test_debounce(self):
        ctl = ChargeController(ChargerConfig(damping=DampingConfig(term_debounce_steps=3)))
        s = st(soc=0.99, voltage=4.199, i_prev=0.1)
        self.assertGreater(ctl.step(s).current_a, 0.0)
        self.assertGreater(ctl.step(s).current_a, 0.0)
        self.assertEqual(ctl.step(s).reason, ch.R_DONE)
        self.assertFalse(ctl.is_charging)

    def test_noise_resets_debounce(self):
        ctl = ChargeController(ChargerConfig(damping=DampingConfig(term_debounce_steps=2)))
        term = st(soc=0.99, voltage=4.199, i_prev=0.1)
        ctl.step(term)
        ctl.step(st(soc=0.5, voltage=3.8, i_prev=2.0))  # normal sample
        ctl.step(term)
        self.assertTrue(ctl.is_charging)

    def test_latched_until_soc_drops(self):
        ctl = ChargeController(ChargerConfig(damping=DampingConfig(term_debounce_steps=1)))
        ctl.step(st(soc=0.93, voltage=4.199, i_prev=0.1))   # early CV termination
        self.assertFalse(ctl.is_charging)
        # relaxed voltage would allow current, but SOC hasn't dropped 5 %
        self.assertEqual(ctl.step(st(soc=0.92, voltage=4.10, i_prev=0.0)).reason, ch.R_DONE)
        self.assertGreater(ctl.step(st(soc=0.879, voltage=4.05, i_prev=0.0)).current_a, 0.0)


class TestCvDamping(unittest.TestCase):
    """Asymmetric CV damping restored from the original firmware."""

    def setUp(self):
        self.cfg = ChargerConfig()

    def raw(self, s):
        v_limit = ch._limit_voltage(s, self.cfg)
        return v_limit, (v_limit - ch._estimate_ocv(s)) / s.r_internal

    def test_approach_branch_rises_but_never_exceeds_ceiling(self):
        s = st(soc=0.6, voltage=4.19, i_prev=1.0)          # 10 mV below limit
        v_limit, i_raw = self.raw(s)                        # i_raw = 1.2 A
        i = ch._cv_limit(s, ch._estimate_ocv(s), v_limit, i_target=6.75, cfg=self.cfg)
        self.assertAlmostEqual(i, 0.7 * 1.0 + 0.3 * i_raw)
        self.assertGreater(i, s.i_prev)
        self.assertLess(i, i_raw)

    def test_no_smoothing_when_cv_not_binding(self):
        s = st(soc=0.3, voltage=3.70, i_prev=0.8)
        v_limit, i_raw = self.raw(s)
        i = ch._cv_limit(s, ch._estimate_ocv(s), v_limit, i_target=6.75, cfg=self.cfg)
        self.assertAlmostEqual(i, i_raw)

    def test_minor_overvoltage_inertia_configurable(self):
        s = st(soc=0.6, voltage=4.205, i_prev=1.0)         # 5 mV over
        v_limit, i_raw = self.raw(s)
        ocv = ch._estimate_ocv(s)
        self.assertAlmostEqual(ch._cv_limit(s, ocv, v_limit, 6.75, self.cfg), i_raw)
        cfg_orig = ChargerConfig(damping=DampingConfig(cv_minor_ov_inertia=0.2))
        self.assertAlmostEqual(ch._cv_limit(s, ocv, v_limit, 6.75, cfg_orig),
                               0.2 * 1.0 + 0.8 * i_raw)

    def test_major_overvoltage_immediate_even_with_inertia(self):
        s = st(soc=0.6, voltage=4.215, i_prev=1.0)         # 15 mV over
        v_limit, i_raw = self.raw(s)
        cfg = ChargerConfig(damping=DampingConfig(cv_minor_ov_inertia=0.2))
        self.assertAlmostEqual(ch._cv_limit(s, ch._estimate_ocv(s), v_limit, 6.75, cfg), i_raw)

    def test_bad_damping_config(self):
        for kw in ({"cv_approach_gain": 0.0}, {"cv_approach_gain": 1.5},
                   {"cv_minor_ov_inertia": 1.0}):
            with self.subTest(**kw), self.assertRaises(ValueError):
                DampingConfig(**kw)

    @staticmethod
    def _hf_jitter(res):
        """Mean |2nd difference| of current while charging (removes the trend)."""
        on = [k for k, c in enumerate(res.current) if c > 0]
        s = res.current[:on[-1] + 1]
        return sum(abs(s[k + 1] - 2 * s[k] + s[k - 1]) for k in range(1, len(s) - 1)) / (len(s) - 2)

    @staticmethod
    def _max_true_ov(res):
        return max(v - vl for v, vl in zip(res.voltage, res.v_limit) if vl is not None)

    def test_smoothing_reduces_jitter_under_measurement_noise(self):
        def run(gain):
            cfg = ChargerConfig(damping=DampingConfig(cv_approach_gain=gain))
            rs = [simulate(cfg, soc0=0.1,
                           v_noise=0.005, r_noise=0.05, seed=s) for s in range(5)]
            for r in rs:
                self.assertEqual(r.reason[-1], ch.R_DONE)
            return (sum(self._hf_jitter(r) for r in rs) / len(rs),
                    max(self._max_true_ov(r) for r in rs))

        jit_raw, ov_raw = run(1.0)       # no smoothing
        jit_smooth, ov_smooth = run(0.3)  # original design
        self.assertLess(jit_smooth, 0.8 * jit_raw)
        self.assertLess(ov_smooth, ov_raw)


class TestClosedLoop(unittest.TestCase):
    def check(self, cfg, **kw):
        res = simulate(cfg, **kw)
        limits = [v for v in res.v_limit if v is not None]
        # One‑sample lag: SOC keeps rising during dt, so the next measured
        # voltage can exceed the limit by a few mV (≈ dt·I·dOCV/dSOC).
        # Allow 5 mV at dt = 10 s; the hardware CV loop is the primary guard.
        for v, vl in zip(res.voltage, res.v_limit):
            if vl is not None:
                self.assertLessEqual(v, vl + 0.005)
        self.assertEqual(res.reason[-1], ch.R_DONE, "charge must terminate and stay done")
        # no restart after termination
        first_done = res.reason.index(ch.R_DONE)
        self.assertTrue(all(c == 0.0 for c in res.current[first_done:]))
        return res, limits

    def test_full_charge_25c(self):
        res, _ = self.check(ChargerConfig(), soc0=0.05)
        self.assertGreater(max(res.soc), 0.98)

    def test_hard_cap_80(self):
        res, _ = self.check(ChargerConfig(hard_cap_soc=0.8), soc0=0.1)
        self.assertLessEqual(max(res.soc), 0.8 + 1e-3)

    def test_cold_and_aged(self):
        res, _ = self.check(ChargerConfig(), soc0=0.1, temp_c=5.0, soh=0.8, r=0.08)
        self.assertLessEqual(max(res.current), 0.2 * 4.5 + 1e-9)

    def test_high_voltage_cell(self):
        self.check(ChargerConfig(v_max_new=4.40), soc0=0.1)


if __name__ == "__main__":
    unittest.main()
