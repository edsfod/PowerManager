/* Standalone browser Mock bridge (used when opened directly without WebView2 host) */
window.createPowerManagerMockHost = function createPowerManagerMockHost(onRefresh, onToast) {
  const fmtHm = d => `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  const state = {
    agent: true,
    dry_run: false,
    forced_sleep: false,
    rules_expanded: false,
    data_dir: 'C:\\Users\\Demo\\AppData\\Local\\PowerManager',
    mode: { mode: 'normal', hours: 12, until: null, left: null, run_until_utc: null },
    power_mode: { ac: '最佳性能', dc: '平衡' },
    battery: {
      pct: 78, ac: true, flow: '插着电，停在 75%–80%（保护电池）',
      band: [75, 80], hw: [75, 80],
      temp: 29.4, system_w: 18, charge_ma: 0, hottest: 44,
      read_at: fmtHm(new Date()), read_age_min: 2, slow: false,
      last: '上次睡眠：2 分钟（合盖 1 分钟 · 开盖唤醒 · 78%→78%）'
    },
    boost: { active: false },
    boost_prefs: { target: 95, hold: 60, maxh: 4 }
  };

  function applyPreset(name) {
    state.agent = true;
    state.forced_sleep = false;
    state.boost = { active: false };
    state.mode = { mode: 'normal', hours: 12, until: null, left: null, run_until_utc: null };
    state.battery.band = [75, 80];
    state.battery.hw = [75, 80];
    state.battery.ac = true;
    state.battery.pct = 78;
    state.battery.read_age_min = 2;
    state.battery.flow = '插着电，停在 75%–80%（保护电池）';
    if (name === 'run') {
      const end = new Date(Date.now() + 4 * 3600 * 1000);
      state.mode = { mode: 'run', hours: 4, until: fmtHm(end), left: '4 小时 0 分', run_until_utc: end.toISOString() };
    } else if (name === 'boost') {
      const end = new Date(Date.now() + 4 * 3600 * 1000);
      state.battery.pct = 88;
      state.battery.band = [90, 95];
      state.battery.hw = [90, 95];
      state.battery.charge_ma = 2400;
      state.battery.flow = '正在充电（2400 mA）';
      state.boost = { active: true, target: 95, hold: 60, held: null, full_since_utc: null, deadline: fmtHm(end) };
    } else if (name === 'forced') {
      state.forced_sleep = true;
      state.battery.ac = false;
      state.battery.pct = 46;
      state.battery.flow = '用电池中';
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
          const hours = params.hours != null ? Number(params.hours) : 12;
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
          state.battery.band = [target - 5, target];
          state.battery.hw = [target - 5, target];
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
