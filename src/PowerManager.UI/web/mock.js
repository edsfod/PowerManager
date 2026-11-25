/* Standalone browser Mock bridge (used when opened directly without WebView2 host) */
window.createPowerManagerMockHost = function createPowerManagerMockHost(onRefresh, onToast) {
  const fmtHm = d => `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  // 与 PowerManager.Core.Policy.PolicySnapshot 保持一致（由 UiPolicyConsistencyTests 校验，改动须同步）
  /* policy:begin */
  const policy = {
    run_hours_min: 1, run_hours_max: 24, run_hours_default: 12,
    boost_target_min: 85, boost_target_max: 100, boost_hold_min: 15, boost_hold_max: 180,
    boost_maxh_min: 1, boost_maxh_max: 12, boost_hysteresis: 5,
    charge_start: 75, charge_stop: 80,
    normal_ac_display_sec: 900, normal_ac_sleep_sec: 3600, normal_dc_display_sec: 300, normal_dc_sleep_sec: 900,
    run_ac_display_sec: 900, run_ac_sleep_sec: 0, run_dc_display_sec: 300, run_dc_sleep_sec: 1800,
    lid_sleep_delay_sec: 60, hibernate_fallback_sec: 120, commute_temp_c: 40,
    dc_balanced_min_pct: 50, history_step_pct: 5
  };
  /* policy:end */
  const baseBand = () => [policy.charge_start, policy.charge_stop];
  const state = {
    agent: true,
    dry_run: false,
    forced_sleep: false,
    rules_expanded: false,
    data_dir: 'C:\\Users\\Demo\\AppData\\Local\\PowerManager',
    mode: { mode: 'normal', hours: policy.run_hours_default, until: null, left: null, run_until_utc: null },
    power_mode: { ac: '最佳性能', dc: '平衡' },
    battery: {
      pct: 78, ac: true, flow: '接通电源 · 已到 80%，不再充',
      band: baseBand(), hw: baseBand(),
      temp: 29.4, system_w: 18, charge_ma: 0, hottest: 44,
      read_at: fmtHm(new Date()), read_age_min: 2, slow: false,
      last: '上次冲高已回落：充到 95% 后插电保持满 60 分钟'
    },
    boost: { active: false },
    boost_prefs: { target: 95, hold: 60, maxh: 4 },
    policy
  };

  function applyPreset(name) {
    state.agent = true;
    state.forced_sleep = false;
    state.boost = { active: false };
    state.mode = { mode: 'normal', hours: policy.run_hours_default, until: null, left: null, run_until_utc: null };
    state.battery.band = baseBand();
    state.battery.hw = baseBand();
    state.battery.ac = true;
    state.battery.pct = 78;
    state.battery.read_age_min = 2;
    state.battery.flow = '接通电源 · 已到 80%，不再充';
    if (name === 'run') {
      const end = new Date(Date.now() + 4 * 3600 * 1000);
      state.mode = { mode: 'run', hours: 4, until: fmtHm(end), left: '4 小时 0 分', run_until_utc: end.toISOString() };
    } else if (name === 'boost') {
      const end = new Date(Date.now() + 4 * 3600 * 1000);
      state.battery.pct = 88;
      state.battery.band = [95 - policy.boost_hysteresis, 95];
      state.battery.hw = [95 - policy.boost_hysteresis, 95];
      state.battery.charge_ma = 2400;
      state.battery.flow = '接通电源 · 正在充电 36.5 W';
      state.boost = { active: true, target: 95, hold: 60, held: null, full_since_utc: null, deadline: fmtHm(end) };
    } else if (name === 'forced') {
      state.forced_sleep = true;
      state.battery.ac = false;
      state.battery.pct = 46;
      state.battery.flow = '使用电池 · 放电 11.2 W';
      state.power_mode.dc = '最佳能效';
    } else if (name === 'offline') {
      state.agent = false;
      state.battery.read_age_min = 45;
    }
  }

  function initToolbar() {
    const qs = new URLSearchParams(window.location.search);
    const preset = qs.get('preset');
    if (preset) applyPreset(preset);
    if (qs.get('expanded') === '1') state.rules_expanded = true;
    if (qs.get('maximized') === '1') document.documentElement.dataset.windowState = 'maximized';

    const bar = document.getElementById('mock-bar');
    const box = document.getElementById('mock-presets');
    if (qs.get('hideMockBar') !== '1') {
      bar.classList.remove('hidden');
    }
    const items = [
      ['正常', () => applyPreset('normal')],
      ['保持运行', () => applyPreset('run')],
      ['冲高中', () => applyPreset('boost')],
      ['主动睡眠', () => applyPreset('forced')],
      ['常驻离线', () => applyPreset('offline')],
      ['浅/深色', () => {
        const root = document.documentElement;
        const cur = root.getAttribute('data-theme');
        const next = cur === 'dark' ? 'light' : 'dark';
        root.setAttribute('data-theme', next);
      }]
    ];
    for (const [label, fn] of items) {
      const b = document.createElement('button');
      b.type = 'button';
      b.className = 'chip';
      b.textContent = label;
      b.onclick = () => { fn(); onRefresh(); };
      box.appendChild(b);
    }

    if (qs.get('audit') === '1') {
      const reportMetrics = () => {
        if (qs.get('draft') === '1') {
          const runTile = document.getElementById('t-run');
          if (runTile && !runTile.classList.contains('on') && !runTile.classList.contains('draft')) {
            runTile.click();
          }
        }
        const root = document.documentElement;
        const main = document.querySelector('main');
        document.body.dataset.scrollHeight = String(root.scrollHeight);
        document.body.dataset.clientHeight = String(root.clientHeight);
        document.body.dataset.mainHeight = String(main ? Math.ceil(main.getBoundingClientRect().height) : 0);
      };
      setTimeout(reportMetrics, 250);
    }
  }

  return {
    initToolbar,
    async call(method, params) {
      switch (method) {
        case 'get_state':
          return JSON.parse(JSON.stringify(state));
        case 'set_mode': {
          const mode = params.mode || 'normal';
          const hours = params.hours != null ? Number(params.hours) : policy.run_hours_default;
          if (mode === 'run') {
            if (hours === 0) {
              state.mode = { mode: 'run', hours: 0, until: null, left: null, run_until_utc: null };
            } else {
              const end = new Date(Date.now() + hours * 3600 * 1000);
              state.mode = { mode: 'run', hours, until: fmtHm(end), left: `${hours} 小时 0 分`, run_until_utc: end.toISOString() };
            }
          } else {
            state.mode = { mode: 'normal', hours: state.mode.hours, until: null, left: null, run_until_utc: null };
          }
          return { ok: true };
        }
        case 'boost': {
          const target = Number(params.target || 95);
          const hold = Number(params.hold || 60);
          const maxh = Number(params.maxh || 4);
          const end = new Date(Date.now() + maxh * 3600 * 1000);
          state.boost_prefs = { target, hold, maxh };
          state.battery.band = [target - policy.boost_hysteresis, target];
          state.battery.hw = [target - policy.boost_hysteresis, target];
          state.boost = { active: true, target, hold, held: null, full_since_utc: null, deadline: fmtHm(end) };
          return { ok: true, msg: `Mock：已开始冲高到 ${target}%` };
        }
        case 'refresh_ec':
          state.battery.read_at = fmtHm(new Date());
          state.battery.read_age_min = 0;
          return { ok: true, msg: 'Mock：已刷新 EC 读数' };
        case 'open_data_dir':
          return { ok: true, msg: `Mock：数据目录 ${state.data_dir}` };
        case 'start_agent':
          state.agent = true;
          return { ok: true };
        case 'set_expanded':
          state.rules_expanded = !!params.expanded;
          return { ok: true };
        case 'set_content_height':
          return { ok: true };
        case 'close_window':
          onToast('Mock 模式：Esc 关闭窗口');
          return { ok: true };
        case 'smoke_ready':
          return { ok: true };
        default:
          throw new Error(`未知方法：${method}`);
      }
    }
  };
};
