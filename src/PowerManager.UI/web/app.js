const $ = id => document.getElementById(id);
let S = null;
const pick = { target: null, hold: null, maxh: null };
const RUN_HOUR_PRESETS = [2, 4, 8, 12, 24, 0];
const runDraft = { active: false, hours: null };

/* WebView2 JSON-RPC bridge with standalone browser Mock fallback */
const hasWebViewHost = !!(window.chrome && window.chrome.webview);
let rpcSeq = 1;
const rpcPending = new Map();

const mockHost = !hasWebViewHost && typeof window.createPowerManagerMockHost === 'function'
  ? window.createPowerManagerMockHost(() => refresh(), msg => toast(msg))
  : null;

function callHost(method, params) {
  if (!hasWebViewHost) {
    if (!mockHost) return Promise.reject(new Error('缺少 WebView2 宿主或 Mock 桥接'));
    return mockHost.call(method, params ?? {});
  }
  const id = rpcSeq++;
  return new Promise((resolve, reject) => {
    rpcPending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, method, params: params ?? {} });
  });
}

if (hasWebViewHost) {
  window.chrome.webview.addEventListener('message', ev => {
    const msg = ev.data;
    if (!msg) return;
    if (msg.event === 'focus') {
      refresh();
      return;
    }
    if (msg.event === 'window_state') {
      const max = !!msg.maximized;
      document.documentElement.dataset.windowState = max ? 'maximized' : 'normal';
      if (!max) {
        lastReportedHeight = 0;
        scheduleContentHeightSync();
      }
      return;
    }
    if (msg.id != null && rpcPending.has(msg.id)) {
      const { resolve, reject } = rpcPending.get(msg.id);
      rpcPending.delete(msg.id);
      if (msg.error) reject(new Error(msg.error));
      else resolve(msg.result);
    }
  });
}

const api = {
  get_state: () => callHost('get_state'),
  set_mode: (mode, hours) => callHost('set_mode', { mode, hours }),
  boost: (target, hold, maxh) => callHost('boost', { target, hold, maxh }),
  refresh_ec: () => callHost('refresh_ec'),
  open_data_dir: () => callHost('open_data_dir'),
  start_agent: () => callHost('start_agent'),
  set_expanded: expanded => callHost('set_expanded', { expanded }),
  set_content_height: height => callHost('set_content_height', { height }),
  close_window: () => callHost('close_window'),
  smoke_ready: () => callHost('smoke_ready'),
};

function toast(msg) {
  const t = $('toast'); t.textContent = msg; t.classList.add('show');
  clearTimeout(t._h); t._h = setTimeout(() => t.classList.remove('show'), 3500);
}

function chips(el, values, current, fmt, onPick) {
  el.innerHTML = '';
  for (const v of values) {
    const b = document.createElement('button');
    b.className = 'chip num' + (v == current ? ' on' : '');
    b.textContent = fmt(v);
    b.onclick = () => onPick(v);
    el.appendChild(b);
  }
}

function formatLeftFromUtc(isoUtc) {
  const ms = Date.parse(isoUtc) - Date.now();
  if (Number.isNaN(ms) || ms <= 0) return '即将到期';
  const s = Math.floor(ms / 1000);
  return s >= 3600 ? `${Math.floor(s / 3600)} 小时 ${Math.floor((s % 3600) / 60)} 分` : `${Math.max(1, Math.floor(s / 60))} 分钟`;
}

function formatEndHmAfterHours(hours) {
  const d = new Date(Date.now() + hours * 3600 * 1000);
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

function renderModeSection() {
  if (!S) return;
  const run = S.mode.mode === 'run';
  const draftForNewRun = !run && runDraft.active;
  const draftHours = runDraft.active && runDraft.hours != null ? runDraft.hours : S.mode.hours;
  const draftForEdit = run && runDraft.active && draftHours !== S.mode.hours;
  const showRunbar = run || draftForNewRun;

  $('t-normal').classList.toggle('on', !run && !draftForNewRun);
  $('t-run').classList.toggle('on', run);
  $('t-run').classList.toggle('draft', draftForNewRun);
  $('run-draft-badge').classList.toggle('hidden', !draftForNewRun);

  const runbar = $('runbar');
  runbar.classList.toggle('hidden', !showRunbar);
  runbar.classList.toggle('draft', draftForNewRun || draftForEdit);
  $('forced-sleep-bar').classList.toggle('hidden', !S.forced_sleep);

  if (showRunbar) {
    const isIndefinite = draftHours === 0;
    const activeIndefinite = run && !S.mode.run_until_utc;
    const endPreview = isIndefinite ? null : formatEndHmAfterHours(draftHours);
    if (draftForNewRun) {
      $('run-left').textContent = isIndefinite
        ? '待确认：保持运行，直到手动改回'
        : `待确认：保持运行 ${draftHours} 小时（预计 ${endPreview} 回到普通）`;
      $('b-run-confirm').textContent = '确认';
      $('run-actions').classList.remove('hidden');
    } else if (draftForEdit) {
      const curText = activeIndefinite
        ? '保持运行中'
        : `还剩 ${S.mode.run_until_utc ? formatLeftFromUtc(S.mode.run_until_utc) : S.mode.left}`;
      $('run-left').textContent = isIndefinite
        ? `${curText}（待确认：改为直到手动改回）`
        : `${curText}（待确认：改为 ${draftHours} 小时，约 ${endPreview} 回到普通）`;
      $('b-run-confirm').textContent = '确认';
      $('run-actions').classList.remove('hidden');
    } else {
      if (activeIndefinite) {
        $('run-left').textContent = '保持运行中，直到手动改回';
      } else {
        const leftText = S.mode.run_until_utc ? formatLeftFromUtc(S.mode.run_until_utc) : S.mode.left;
        $('run-left').textContent = `还剩 ${leftText}，${S.mode.until} 回到普通`;
      }
      $('run-actions').classList.add('hidden');
    }

    const r = $('r-hours');
    const sliderRow = r.closest('.slider-row');
    if (sliderRow) sliderRow.classList.toggle('muted-slider', isIndefinite);
    if (!isIndefinite && Number(r.value) !== draftHours) r.value = draftHours;
    $('v-hours').textContent = isIndefinite ? '直到手动改回' : `${draftHours} 小时`;
    chips($('hours'), RUN_HOUR_PRESETS, draftHours, v => (v === 0 ? '直到手动改回' : `${v} 小时`), v => {
      runDraft.active = true;
      runDraft.hours = v;
      renderModeSection();
    });
  }

  const showRunRules = run || draftForNewRun;
  const R = showRunRules
    ? [['插电', '15 分钟关屏，不睡；合盖继续跑'], ['用电池', '5 分钟关屏，30 分钟没人操作才睡；电池到 40 °C 提前睡'],
       ['睡不下去', '2 分钟后休眠（后台忙时只能休眠）']]
    : [['合盖', '1 分钟后睡'], ['插电', '15 分钟关屏，1 小时睡'], ['用电池', '5 分钟关屏，15 分钟睡'],
       ['睡不下去', '2 分钟后休眠（后台忙时只能休眠）']];
  R.push(['电源模式', '插电 最佳性能；用电池 ≥ 50% 平衡、< 50% 最佳能效'],
         ['电源键', '不起作用（睡着时按会唤醒）']);
  $('rules').innerHTML = R.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('');
}

function updateLiveCountdowns() {
  if (!S || document.hidden) return;
  if (S.mode.mode === 'run' && S.mode.run_until_utc) {
    renderModeSection();
  }
  if (S.boost.active && S.boost.full_since_utc) {
    const held = Math.max(0, Math.floor((Date.now() - Date.parse(S.boost.full_since_utc)) / 60000));
    $('bo-bar').style.width = Math.min(100, 100 * held / Math.max(1, S.boost.hold)) + '%';
    $('bo-text').textContent = `已充到，插着电保持 ${held}/${S.boost.hold} 分钟；拔电也会回到 75%–80%`;
  }
}

function render(s) {
  S = s;
  // agent
  $('agent').classList.toggle('off', !s.agent);
  const drySuffix = s.dry_run ? ' (dry-run)' : '';
  $('agent-text').textContent = (s.agent ? '常驻运行中' : '常驻没在运行') + drySuffix;
  $('agent-start').classList.toggle('hidden', s.agent || s.dry_run);

  // mode
  renderModeSection();

  // battery
  const b = s.battery;
  $('pct').innerHTML = b.pct == null ? '–' : `${b.pct}<small>%</small>`;
  $('flow').textContent = b.flow;
  $('read-at').textContent = b.read_at ? `上次更新：${b.read_at}` : '';
  $('read-at').classList.toggle('stale', b.read_age_min != null && b.read_age_min >= 30);
  $('read-wrap').classList.toggle('hidden', !b.read_at);
  const [lo, hi] = b.band;
  $('hw').textContent = b.hw
    ? (b.hw[0] == lo && b.hw[1] == hi ? `充电区间 ${lo}%–${hi}%` : `充电区间 ${b.hw[0]}%–${b.hw[1]}%（应为 ${lo}%–${hi}%，下次运行改回）`)
    : '充电区间还没读到';
  const fill = $('fill');
  fill.style.width = (b.pct ?? 0) + '%';
  fill.style.background = (b.pct ?? 0) >= 50 ? 'var(--green)' : (b.pct ?? 0) >= 20 ? 'var(--amber)' : 'var(--red)';
  const band = $('band');
  band.style.left = lo + '%'; band.style.width = (hi - lo) + '%';
  $('band-label').textContent = `${lo}–${hi}%`;
  $('s-temp').textContent = b.temp != null ? `${b.temp.toFixed(1)} °C` : '–';
  $('s-power').textContent = b.system_w != null ? `${b.system_w} W` : '–';
  $('s-ma').textContent = b.charge_ma != null ? `${b.charge_ma} mA` : '–';
  $('s-hot').textContent = b.hottest != null ? `${Math.round(b.hottest)} °C` : '–';
  $('pm-ac-v').textContent = s.power_mode.ac;
  $('pm-dc-v').textContent = s.power_mode.dc;
  $('pm-ac').classList.toggle('on', !!b.ac);
  $('pm-dc').classList.toggle('on', !b.ac);
  const note = $('note');
  note.textContent = b.slow ? '充得慢：机器热或功耗高时 EC 会压低充电电流，凉下来自动放开' : (b.last || '');
  note.classList.toggle('warn', !!b.slow);

  // boost
  const bo = s.boost;
  $('boost-on').classList.toggle('hidden', !bo.active);
  $('boost-off').classList.toggle('hidden', bo.active);
  if (bo.active) {
    $('bo-title').textContent = `冲高到 ${bo.target}%`;
    $('bo-deadline').textContent = bo.deadline ? `最迟 ${bo.deadline} 回落` : '';
    if (bo.held != null) {
      $('bo-bar').style.width = Math.min(100, 100 * bo.held / Math.max(1, bo.hold)) + '%';
      $('bo-text').textContent = `已充到，插着电保持 ${bo.held}/${bo.hold} 分钟；拔电也会回到 75%–80%`;
    } else {
      $('bo-bar').style.width = Math.min(100, 100 * Math.max(0, (b.pct ?? 0) - lo) / Math.max(1, bo.target - lo)) + '%';
      $('bo-text').textContent = `正在充（${b.pct ?? '–'}%），充到后插着电保持 ${bo.hold} 分钟；拔电也会回到 75%–80%`;
    }
  } else {
    pick.target ??= s.boost_prefs.target;
    pick.hold ??= s.boost_prefs.hold;
    pick.maxh ??= s.boost_prefs.maxh;
    renderBoostControls();
  }
}

const BOOST_FIELDS = [
  { key: 'target', range: 'r-target', val: 'v-target', chips: 'b-target', presets: [85, 90, 95, 100], fmt: v => `${v}%` },
  { key: 'hold',   range: 'r-hold',   val: 'v-hold',   chips: 'b-hold',   presets: [30, 60, 120],     fmt: v => `${v} 分钟` },
  { key: 'maxh',   range: 'r-max',    val: 'v-max',    chips: 'b-max',    presets: [2, 4, 8],         fmt: v => `${v} 小时` },
];

function renderBoostControls() {
  for (const f of BOOST_FIELDS) {
    const cur = pick[f.key];
    const r = $(f.range);
    if (Number(r.value) !== cur) r.value = cur;
    $(f.val).textContent = f.fmt(cur);
    chips($(f.chips), f.presets, cur, f.fmt, v => { pick[f.key] = v; renderBoostControls(); });
  }
}

for (const f of BOOST_FIELDS) {
  $(f.range).addEventListener('input', e => {
    pick[f.key] = Number(e.target.value);
    $(f.val).textContent = f.fmt(pick[f.key]);
    [...$(f.chips).children].forEach((b, i) => b.classList.toggle('on', f.presets[i] === pick[f.key]));
  });
}

let firstLoaded = false;
async function refresh() {
  try {
    const state = await api.get_state();
    if (!firstLoaded && state.rules_expanded) {
      setExpandedUI(true);
    }
    render(state);
    if (!firstLoaded) {
      firstLoaded = true;
      await api.smoke_ready();
    }
  } catch (err) {
    toast(err.message || '读取状态失败');
  }
}

async function setMode(mode, hours) {
  try {
    const r = await api.set_mode(mode, hours ?? S.mode.hours);
    if (!r.ok) toast(r.msg || '切换模式失败');
  } catch (err) {
    toast(err.message || '切换模式失败');
  }
  await refresh();
}

let animatingUntil = 0;
function setExpandedUI(on) {
  $('mode-card').classList.toggle('expanded', on);
  document.querySelector('main').classList.toggle('expanded', on);
}
$('rules-toggle').onclick = () => {
  const on = !$('mode-card').classList.contains('expanded');
  animatingUntil = Date.now() + 280;
  setExpandedUI(on);
  api.set_expanded(on).catch(() => {});
};
function syncExpandedByWidth() {
  if (Date.now() < animatingUntil) return;
  const wide = window.innerWidth >= 880;
  if (wide !== $('mode-card').classList.contains('expanded')) setExpandedUI(wide);
}
window.addEventListener('resize', syncExpandedByWidth);
$('t-normal').onclick = () => {
  if (!S) return;
  if (S.mode.mode === 'normal') {
    if (runDraft.active) {
      runDraft.active = false;
      runDraft.hours = null;
      renderModeSection();
    }
    return;
  }
  runDraft.active = false;
  runDraft.hours = null;
  setMode('normal');
};
$('t-run').onclick = () => {
  if (!S || S.mode.mode === 'run') return;
  if (!runDraft.active) {
    runDraft.active = true;
    runDraft.hours = S.mode.hours || 12;
    renderModeSection();
  }
};
$('r-hours').addEventListener('input', e => {
  if (!S) return;
  runDraft.active = true;
  runDraft.hours = Number(e.target.value);
  renderModeSection();
});
$('b-run-cancel').onclick = () => {
  runDraft.active = false;
  runDraft.hours = null;
  renderModeSection();
};
$('b-run-confirm').onclick = async () => {
  if (!S) return;
  const hours = runDraft.hours ?? S.mode.hours ?? 12;
  const btn = $('b-run-confirm');
  btn.disabled = true;
  try {
    await setMode('run', hours);
    runDraft.active = false;
    runDraft.hours = null;
    renderModeSection();
  } finally {
    btn.disabled = false;
  }
};
$('agent-start').onclick = async () => {
  try {
    const r = await api.start_agent();
    if (!r.ok) toast(r.msg || '启动失败');
  } catch (err) {
    toast(err.message || '启动失败');
  }
  await refresh();
};
$('b-refresh-ec').onclick = async () => {
  const btn = $('b-refresh-ec');
  btn.disabled = true; btn.textContent = '刷新中…';
  try {
    const r = await api.refresh_ec();
    toast(r.ok ? (r.msg || '已刷新 EC 读数') : (r.msg || '刷新失败'));
  } catch (err) {
    toast(err.message || '刷新失败');
  } finally {
    btn.disabled = false; btn.textContent = '刷新读数';
  }
  await refresh();
};
$('open-data-dir').onclick = async () => {
  try {
    const r = await api.open_data_dir();
    if (!r.ok) toast(r.msg || '无法打开目录');
  } catch (err) {
    toast(err.message || '无法打开目录');
  }
};
$('b-go').onclick = async () => {
  const btn = $('b-go'); btn.disabled = true; btn.textContent = '正在写入 EC…';
  try {
    const r = await api.boost(pick.target, pick.hold, pick.maxh);
    toast(r.ok ? (r.msg || `已开始冲高到 ${pick.target}%`) : (r.msg || '发起冲高失败'));
  } catch (err) {
    toast(err.message || '发起冲高失败');
  } finally {
    btn.disabled = false; btn.textContent = '开始冲高';
  }
  await refresh();
};

window.addEventListener('keydown', e => {
  if (e.key === 'Escape') {
    e.preventDefault();
    api.close_window().catch(() => {});
  } else if (e.key === 'F5') {
    e.preventDefault();
    refresh();
  }
});

// 仅在窗口可见时本地重算剩余时间，零 IPC、零磁盘、零 EC 唤醒
setInterval(updateLiveCountdowns, 30000);

let lastReportedHeight = 0;
let heightRaf = 0;
function scheduleContentHeightSync() {
  if (!hasWebViewHost || heightRaf || document.documentElement.dataset.windowState === 'maximized') return;
  heightRaf = requestAnimationFrame(() => {
    heightRaf = 0;
    const main = document.querySelector('main');
    if (!main) return;
    const h = Math.ceil(main.getBoundingClientRect().height);
    if (h > 0 && Math.abs(h - lastReportedHeight) >= 2) {
      lastReportedHeight = h;
      api.set_content_height(h).catch(() => {});
    }
  });
}

document.addEventListener('DOMContentLoaded', () => {
  if (mockHost) mockHost.initToolbar();
  syncExpandedByWidth();
  const main = document.querySelector('main');
  if (main && typeof ResizeObserver === 'function') {
    new ResizeObserver(scheduleContentHeightSync).observe(main);
  }
  refresh();
});
window.addEventListener('focus', refresh);
document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
