const state = {
  status: null,
  kinds: [],
  jobs: [],
  findings: [],
  stream: null,
  tick: null,
  logElement: null,
  logLines: [],
  autoScroll: true,
  job: null,
  jobView: null,
  logView: null,
};

const previewTimers = new WeakMap();
const dialog = document.getElementById('dialog');

const $ = (selector, root = document) => root.querySelector(selector);

function el(tag, attributes = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attributes)) {
    if (value === null || value === undefined || value === false) continue;
    if (key === 'class') node.className = value;
    else if (key === 'text') node.textContent = value;
    else if (key === 'checked' && node instanceof HTMLInputElement) node.checked = Boolean(value);
    else if (key === 'value' && (node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement)) node.value = value;
    else if (key.startsWith('on') && typeof value === 'function') node.addEventListener(key.slice(2).toLowerCase(), value);
    else if (value === true) node.setAttribute(key, '');
    else node.setAttribute(key, value);
  }
  append(node, children);
  return node;
}

function append(node, children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: { 'Content-Type': 'application/json', 'X-Wyl-Dashboard': '1', ...(options.headers || {}) },
  });
  const text = await response.text();
  const contentType = response.headers.get('content-type') || '';
  let data = text;
  if (contentType.includes('application/json')) {
    try {
      data = text ? JSON.parse(text) : null;
    } catch {
      data = { error: text };
    }
  }
  if (!response.ok)
    throw new Error((data && typeof data === 'object' && data.error) || text || response.statusText);
  return data;
}

function toast(message, bad = false) {
  const element = document.getElementById('toast');
  element.textContent = message;
  element.className = 'toast' + (bad ? ' bad' : '');
  element.hidden = false;
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => { element.hidden = true; }, 3500);
}

async function copy(text) {
  try {
    await navigator.clipboard.writeText(text);
    toast('Copied');
  } catch {
    try {
      const area = el('textarea', {});
      area.value = text;
      area.style.position = 'fixed';
      area.style.opacity = '0';
      document.body.append(area);
      area.select();
      document.execCommand('copy');
      area.remove();
      toast('Copied');
    } catch {
      toast('Could not copy', true);
    }
  }
}

function setTitle(title, meta = '') {
  document.getElementById('pageTitle').textContent = title;
  document.getElementById('topMeta').textContent = meta;
}

function setActiveNav(page) {
  for (const link of document.querySelectorAll('#nav a'))
    link.classList.toggle('active', link.dataset.page === page);
}

function plural(count, one, many) {
  return `${count} ${count === 1 ? one : many}`;
}

function formatNumber(value) {
  return (value ?? 0).toLocaleString();
}

function ago(value) {
  if (!value) return '—';
  const seconds = (Date.now() - new Date(value).getTime()) / 1000;
  if (seconds < 10) return 'just now';
  if (seconds < 60) return `${Math.floor(seconds)}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

function duration(job) {
  if (!job.startedAt) return '—';
  const end = job.endedAt ? new Date(job.endedAt) : new Date();
  const seconds = Math.max(0, (end - new Date(job.startedAt)) / 1000);
  if (seconds < 60) return `${seconds.toFixed(0)}s`;
  return `${Math.floor(seconds / 60)}m ${Math.floor(seconds % 60)}s`;
}

function stateClass(value) {
  return { queued: 'dim', running: 'run', succeeded: 'ok', findings: 'warn', failed: 'bad', cancelled: 'warn', interrupted: 'warn' }[value] || 'dim';
}

function statePill(value) {
  return el('span', { class: `pill ${stateClass(value)}` }, value);
}

function card(title, ...children) {
  return el('section', { class: 'card' },
    el('h2', { class: 'card-title' }, title),
    el('div', { class: 'card-body' }, ...children));
}

function section(title, ...children) {
  return el('section', { class: 'section' },
    el('h2', { class: 'section-title' }, title),
    ...children);
}

function kv(label, value) {
  return el('div', { class: 'kv' }, el('span', { class: 'kv-label' }, label), el('span', { class: 'kv-value' }, value));
}

function banner(text) {
  return el('div', { class: 'banner' }, text);
}

function metric(label, value) {
  return el('div', { class: 'metric' },
    el('div', { class: 'metric-value' }, String(value)),
    el('div', { class: 'metric-label' }, label));
}

function jobSummary(job) {
  const metrics = job.metrics || {};
  if (metrics.failed !== null && metrics.failed !== undefined)
    return `${metrics.passed ?? 0} passed, ${metrics.failed} failed`;
  if (metrics.findings !== null && metrics.findings !== undefined)
    return `${metrics.mutants ?? 0} mutants, ${metrics.findings} findings`;
  if (metrics.survived !== null && metrics.survived !== undefined)
    return `${metrics.killed ?? 0} killed, ${metrics.survived} survived`;
  if (job.error) return job.error;
  if (job.exitCode !== null && job.exitCode !== undefined) return `exit ${job.exitCode}`;
  return job.state;
}

function scoreClass(value) {
  if (value >= 90) return 'ok';
  if (value >= 70) return 'warn';
  return 'bad';
}

function jobTable(jobs) {
  const body = el('tbody');
  for (const job of jobs) {
    body.append(el('tr', {},
      el('td', {}, el('a', { href: `#/job/${job.id}` }, job.title), el('div', { class: 'muted small mono' }, job.kind)),
      el('td', {}, statePill(job.state)),
      el('td', { class: 'muted' }, job.startedAt ? ago(job.startedAt) : 'queued'),
      el('td', { class: 'muted' }, duration(job)),
      el('td', {}, jobSummary(job))));
  }
  return el('div', { class: 'table-wrap' },
    el('table', { class: 'table' },
      el('thead', {}, el('tr', {},
        el('th', {}, 'Job'), el('th', {}, 'Status'), el('th', {}, 'Started'), el('th', {}, 'Took'), el('th', {}, 'Result'))),
      body));
}

function seedChips(seeds) {
  return el('div', { class: 'chips' }, seeds.map(seed =>
    el('a', {
      class: 'chip',
      href: `#/fuzz?seed=${seed.seed}&tier=${seed.tier}`,
      title: `${seed.state}, ${plural(seed.findings, 'finding', 'findings')}, ${ago(seed.at)}`,
    },
    `seed ${seed.seed} · tier ${seed.tier}`,
    el('span', { class: 'muted' }, `· ${seed.findings} findings · ${ago(seed.at)}`))));
}

function defaultValues(kind) {
  const values = {};
  for (const parameter of kind.parameters)
    values[parameter.name] = parameter.default ?? (parameter.type === 'bool' ? 'false' : '');
  return values;
}

function storedForm(kind, defaults) {
  try {
    const raw = localStorage.getItem(`wyl.form.${kind}`);
    if (!raw) return defaults;
    const saved = JSON.parse(raw);
    for (const parameter of kind.parameters) {
      const value = saved[parameter.name];
      if (typeof value !== 'string') continue;
      if (parameter.type === 'select' && !(parameter.options || []).some(option => option.value === value)) continue;
      defaults[parameter.name] = value;
    }
  } catch {
  }
  return defaults;
}

function storeForm(kind, values) {
  try { localStorage.setItem(`wyl.form.${kind}`, JSON.stringify(values)); } catch { }
}

function applyQuery(kind, values, query) {
  for (const parameter of kind.parameters) {
    if (!query.has(parameter.name)) continue;
    const value = query.get(parameter.name);
    if (parameter.type === 'select' && !(parameter.options || []).some(option => option.value === value)) continue;
    values[parameter.name] = value;
  }
}

function buildForm(kind, values, onChange, status) {
  const form = el('div', { class: 'form' });
  for (const parameter of kind.parameters) {
    const value = values[parameter.name] ?? parameter.default ?? '';
    let input;
    if (parameter.type === 'select') {
      input = el('select', {});
      for (const option of parameter.options || []) {
        const node = el('option', { value: option.value }, option.label);
        if (option.value === value) node.selected = true;
        if (parameter.name === 'cc' && status) {
          const toolchain = status.toolchains.find(candidate => candidate.id === option.value);
          if (toolchain && !toolchain.available) {
            node.disabled = true;
            node.textContent = `${option.label} — missing`;
            node.title = toolchain.error || '';
          }
        }
        input.append(node);
      }
      input.addEventListener('change', () => onChange(parameter.name, input.value));
    } else if (parameter.type === 'bool') {
      input = el('input', { type: 'checkbox', checked: value === 'true' || value === true });
      input.addEventListener('change', () => onChange(parameter.name, input.checked ? 'true' : 'false'));
    } else if (parameter.type === 'duration') {
      const number = el('input', { type: 'number', step: 'any' });
      const unitKey = `wyl.unit.${kind.kind}.${parameter.name}`;
      let unit = 'seconds';
      try {
        if (localStorage.getItem(unitKey) === 'minutes') unit = 'minutes';
      } catch {
      }
      const unitSelect = el('select', { class: 'unit-select' },
        el('option', { value: 'seconds' }, 'seconds'),
        el('option', { value: 'minutes' }, 'minutes'));
      unitSelect.value = unit;

      const factor = () => unit === 'minutes' ? 60 : 1;
      const display = canonical => {
        const seconds = Number(canonical);
        if (canonical === '' || !Number.isFinite(seconds)) return canonical;
        return unit === 'minutes' ? String(Math.round((seconds / 60) * 100) / 100) : String(seconds);
      };
      const bounds = () => {
        if (parameter.min !== null && parameter.min !== undefined)
          number.min = String(Number((parameter.min / factor()).toFixed(4)));
        if (parameter.max !== null && parameter.max !== undefined)
          number.max = String(Number((parameter.max / factor()).toFixed(4)));
      };

      number.value = display(value);
      bounds();
      number.addEventListener('input', () => {
        const typed = number.value;
        const amount = Number(typed);
        onChange(parameter.name, typed === '' || !Number.isFinite(amount)
          ? typed
          : String(Math.round(amount * factor())));
      });
      unitSelect.addEventListener('change', () => {
        unit = unitSelect.value;
        try { localStorage.setItem(unitKey, unit); } catch { }
        number.value = display(values[parameter.name]);
        bounds();
      });
      input = el('div', { class: 'field-row' }, number, unitSelect);
    } else if (parameter.type === 'number') {
      input = el('input', { type: 'number', value: String(value), min: parameter.min, max: parameter.max });
      input.addEventListener('input', () => onChange(parameter.name, input.value));
    } else {
      input = el('input', { type: 'text', value: String(value), placeholder: parameter.help || '' });
      input.addEventListener('input', () => onChange(parameter.name, input.value));
    }

    if (parameter.type === 'bool') {
      form.append(el('div', { class: 'field field-check' },
        el('label', { class: 'check' }, input, el('span', {}, parameter.label)),
        parameter.help ? el('span', { class: 'help' }, parameter.help) : null));
    } else {
      let control = input;
      if (kind.kind === 'fuzz' && parameter.name === 'seed') {
        const random = el('button', {
          type: 'button',
          class: 'small',
          title: 'Pick a random seed',
          onclick: () => {
            const values = new Uint32Array(1);
            crypto.getRandomValues(values);
            const seed = String(values[0] & 0x7fffffff);
            input.value = seed;
            onChange(parameter.name, seed);
          },
        }, 'Random');
        control = el('div', { class: 'field-row' }, input, random);
      }
      form.append(el('div', { class: 'field' },
        el('label', {}, parameter.label),
        control,
        parameter.help ? el('span', { class: 'help' }, parameter.help) : null));
    }
  }
  return form;
}

function schedulePreview(kind, values, target) {
  const previous = previewTimers.get(target);
  if (previous) clearTimeout(previous);
  previewTimers.set(target, setTimeout(async () => {
    try {
      const data = await api('/api/jobs/preview', {
        method: 'POST',
        body: JSON.stringify({ kind, params: values }),
      });
      target.replaceChildren(...data.commands.map(command => el('code', {}, `$ ${command}`)));
    } catch (error) {
      target.replaceChildren(el('span', { class: 'error-text small' }, error.message));
    }
  }, 180));
}

async function startJob(kind, values) {
  try {
    const job = await api('/api/jobs', {
      method: 'POST',
      body: JSON.stringify({ kind, params: values }),
    });
    location.hash = `#/job/${job.id}`;
  } catch (error) {
    toast(error.message, true);
  }
}

async function cancelJob(id) {
  try {
    await api(`/api/jobs/${encodeURIComponent(id)}/cancel`, { method: 'POST' });
    toast('Cancel requested');
  } catch (error) {
    toast(error.message, true);
  }
}

async function ensureKinds() {
  if (!state.kinds.length) state.kinds = await api('/api/kinds');
  return state.kinds;
}

async function refreshStatus() {
  try {
    state.status = await api('/api/status');
    updateSidebar(state.status);
  } catch {
  }
}

function updateSidebar(status) {
  const host = document.getElementById('sidebarStatus');
  if (!status) {
    host.replaceChildren(el('span', { class: 'pill dim' }, 'checking'));
    return;
  }
  const parts = [
    el('span', {
      class: `pill ${status.build.stale ? 'warn' : 'ok'}`,
      title: status.build.lastBuilt ? `Built ${new Date(status.build.lastBuilt).toLocaleString()}` : '',
    }, status.build.stale ? 'build stale' : 'build current'),
  ];
  if (status.git)
    parts.push(el('span', { class: `pill ${status.git.dirty ? 'warn' : 'ok'}` },
      status.git.branch + (status.git.dirty ? ' · dirty' : '')));
  host.replaceChildren(...parts);
}

function cleanup() {
  if (state.stream) {
    state.stream.close();
    state.stream = null;
  }
  if (state.tick) {
    clearInterval(state.tick);
    state.tick = null;
  }
  state.logElement = null;
  state.job = null;
  state.jobView = null;
  state.logView = null;
  if (dialog.open) dialog.close();
}

async function route() {
  cleanup();
  const raw = (location.hash || '').replace(/^#\/?/, '');
  const [pathPart, queryPart = ''] = raw.split('?');
  const parts = pathPart.split('/').filter(Boolean);
  const page = parts[0] || 'overview';
  const query = new URLSearchParams(queryPart);
  setActiveNav(page);

  const view = el('div');
  document.getElementById('view').replaceChildren(view);
  view.append(el('div', { class: 'loading' }, 'Loading…'));

  try {
    switch (page) {
      case 'overview': await renderOverview(view); break;
      case 'code': await renderCode(view); break;
      case 'tests': await renderTests(view, query); break;
      case 'fuzz': await renderFuzz(view, query); break;
      case 'mutation': await renderMutation(view, query); break;
      case 'findings': await renderFindings(view); break;
      case 'reference': await renderReference(view); break;
      case 'job': await renderJob(view, parts[1]); break;
      default:
        setTitle('Not found', '');
        view.replaceChildren(el('h2', {}, 'Not found'), el('p', { class: 'muted' }, 'There is no page at that address.'));
    }
  } catch (error) {
    view.replaceChildren(el('h2', {}, 'Something went wrong'), el('p', { class: 'error-text' }, error.message));
  }
}

async function renderOverview(view) {
  setTitle('Overview', '');
  const [status, jobs, lines] = await Promise.all([
    api('/api/status'),
    api('/api/jobs'),
    api('/api/lines').catch(() => null),
  ]);
  view.replaceChildren();
  state.status = status;
  state.jobs = jobs;
  updateSidebar(status);
  setTitle('Overview', status.git ? `${status.git.branch}@${status.git.commit}` : status.root);

  const toolchains = status.toolchains.map(toolchain =>
    el('div', { class: 'row between' },
      el('span', {}, toolchain.name),
      el('span', {
        class: `pill ${toolchain.available ? 'ok' : 'bad'}`,
        title: toolchain.error || '',
      }, toolchain.available ? 'available' : 'missing')));

  const buildCard = card('Build',
    el('div', { class: 'row between' },
      el('span', { class: `pill ${status.build.stale ? 'warn' : 'ok'}` }, status.build.stale ? 'stale' : 'current'),
      el('span', { class: 'muted small' }, status.build.lastBuilt ? `built ${ago(status.build.lastBuilt)}` : 'never built')),
    el('p', { class: 'muted small' }, status.build.stale
      ? 'Sources are newer than the test harnesses. Build before running tests.'
      : 'The test harnesses are newer than the sources.'),
    el('div', { class: 'actions' },
      el('button', { class: 'primary', onclick: () => startJob('build', {}) }, 'Build now')));

  const codeCard = lines
    ? card('Code',
        el('div', { class: 'big' }, formatNumber(lines.code)),
        el('p', { class: 'muted small' },
          `code lines in ${plural(lines.files, 'file', 'files')} · ${formatNumber(lines.lines)} lines total`),
        el('div', {}, lines.groups.filter(group => group.files).map(group =>
          kv(group.label, formatNumber(group.code)))),
        el('div', { class: 'actions' }, el('a', { class: 'btn small', href: '#/code' }, 'Breakdown')))
    : card('Code', el('p', { class: 'muted small' }, 'Line counts are unavailable.'));

  view.append(el('div', { class: 'grid' },
    card('Repository',
      kv('Branch', status.git ? status.git.branch : 'not a checkout'),
      kv('Commit', status.git ? `${status.git.commit} · ${status.git.subject}` : '—'),
      kv('Working tree', status.git ? (status.git.dirty ? plural(status.git.dirty, 'changed file', 'changed files') : 'clean') : '—'),
      kv('Root', status.root)),
    card('Toolchains', ...toolchains),
    buildCard,
    card('Open findings',
      el('div', { class: 'big' }, String(status.findings.total)),
      el('p', { class: 'muted small' },
        `${plural(status.findings.wyl, 'finding', 'findings')} and ${plural(status.findings.logs, 'run log', 'run logs')}`),
      el('div', { class: 'actions' }, el('a', { class: 'btn small', href: '#/findings' }, 'Review'))),
    codeCard,
    card('Quick actions',
      el('div', { class: 'actions' },
        el('a', { class: 'btn', href: '#/tests' }, 'Run tests'),
        el('a', { class: 'btn', href: '#/fuzz' }, 'Start fuzzing'),
        el('a', { class: 'btn', href: '#/mutation' }, 'Mutation testing'),
        el('a', { class: 'btn', href: '#/reference' }, 'Commands')))));

  const recent = jobs.slice(0, 8);
  view.append(section('Recent runs', recent.length
    ? jobTable(recent)
    : el('p', { class: 'empty' }, 'No runs yet. Build or run the suite to start a history.')));

  if (status.recentSeeds.length)
    view.append(section('Recent seeds', seedChips(status.recentSeeds)));
}

async function renderCode(view) {
  setTitle('Code', '');
  const [lines, status] = await Promise.all([api('/api/lines'), api('/api/status')]);
  view.replaceChildren();
  state.status = status;
  updateSidebar(status);
  setTitle('Code', status.git ? `${status.git.branch}@${status.git.commit}` : status.root);

  const groups = lines.groups.filter(group => group.files > 0);
  view.append(el('div', { class: 'metrics' },
    metric('All code', formatNumber(lines.code)),
    ...groups.map(group => metric(group.label, formatNumber(group.code)))));
  view.append(el('p', { class: 'muted small' },
    `${plural(lines.files, 'file', 'files')} · ${formatNumber(lines.lines)} physical lines. Code excludes blank and comment-only lines; generated output and dependencies are not counted.`));

  for (const group of groups)
    view.append(section(`${group.label} · ${formatNumber(group.code)} code lines`, lineTable(group)));
}

function lineTable(group) {
  const body = el('tbody');
  for (const item of group.items) {
    body.append(el('tr', {},
      el('td', {},
        el('div', {}, item.name),
        item.path ? el('div', { class: 'muted small mono' }, item.path) : null),
      el('td', { class: 'num' }, formatNumber(item.files)),
      el('td', { class: 'num' }, formatNumber(item.lines)),
      el('td', { class: 'num' }, formatNumber(item.code))));
  }
  body.append(el('tr', { class: 'total' },
    el('td', {}, 'Total'),
    el('td', { class: 'num' }, formatNumber(group.files)),
    el('td', { class: 'num' }, formatNumber(group.lines)),
    el('td', { class: 'num' }, formatNumber(group.code))));

  return el('div', { class: 'table-wrap' },
    el('table', { class: 'table' },
      el('thead', {}, el('tr', {},
        el('th', {}, 'Name'),
        el('th', { class: 'num' }, 'Files'),
        el('th', { class: 'num' }, 'Lines'),
        el('th', { class: 'num' }, 'Code'))),
      body));
}

async function renderTests(view, query) {
  setTitle('Test suite', '');
  const [status, jobs, kinds] = await Promise.all([api('/api/status'), api('/api/jobs'), ensureKinds()]);
  view.replaceChildren();
  state.status = status;
  state.jobs = jobs;
  updateSidebar(status);
  setTitle('Test suite', status.git ? `${status.git.branch}@${status.git.commit}` : '');

  const kind = kinds.find(candidate => candidate.kind === 'suite');
  if (!kind) {
    view.append(el('p', { class: 'error-text' }, 'The suite job is not defined.'));
    return;
  }

  const values = storedForm('suite', defaultValues(kind));
  applyQuery(kind, values, query);
  const available = status.toolchains.filter(toolchain => toolchain.available).map(toolchain => toolchain.id);
  if (!available.includes(values.cc)) values.cc = available[0] || 'msvc';
  storeForm('suite', values);

  const preview = el('div', { class: 'command-preview' });
  const form = buildForm(kind, values, (name, value) => {
    values[name] = value;
    storeForm('suite', values);
    schedulePreview('suite', values, preview);
  }, status);

  view.append(card(kind.title,
    el('p', { class: 'muted small' }, kind.description),
    status.build.stale
      ? banner('The test harness is older than the sources. Turn on “Build first”, or build from the Overview, before running.')
      : null,
    form,
    el('div', { class: 'form-actions' },
      el('button', { class: 'primary', onclick: () => startJob('suite', values) }, 'Run tests'),
      el('span', { class: 'muted small' }, kind.hint || '')),
    el('div', { class: 'label' }, 'Command'),
    preview));
  schedulePreview('suite', values, preview);

  const history = jobs.filter(job => job.kind === 'suite').slice(0, 8);
  view.append(section('Recent suite runs', history.length
    ? jobTable(history)
    : el('p', { class: 'empty' }, 'No suite runs yet.')));
}

async function renderFuzz(view, query) {
  setTitle('Fuzz', '');
  const [status, jobs, kinds] = await Promise.all([api('/api/status'), api('/api/jobs'), ensureKinds()]);
  view.replaceChildren();
  state.status = status;
  state.jobs = jobs;
  updateSidebar(status);
  setTitle('Fuzz', status.git ? `${status.git.branch}@${status.git.commit}` : '');

  const kind = kinds.find(candidate => candidate.kind === 'fuzz');
  const replayKind = kinds.find(candidate => candidate.kind === 'fuzz-replay');
  if (!kind || !replayKind) {
    view.append(el('p', { class: 'error-text' }, 'The fuzz jobs are not defined.'));
    return;
  }

  const values = storedForm('fuzz', defaultValues(kind));
  const replayValues = storedForm('fuzz-replay', defaultValues(replayKind));
  applyQuery(kind, values, query);
  if (query.has('seed')) replayValues.seed = query.get('seed');
  if (query.has('tier')) replayValues.tier = query.get('tier');
  if (query.has('filter')) replayValues.filter = query.get('filter');
  if (query.has('index')) replayValues.index = query.get('index');
  storeForm('fuzz', values);

  const preview = el('div', { class: 'command-preview' });
  const replayPreview = el('div', { class: 'command-preview' });

  const form = buildForm(kind, values, (name, value) => {
    values[name] = value;
    storeForm('fuzz', values);
    schedulePreview('fuzz', values, preview);
    if (name === 'seed') {
      replayValues.seed = value;
      storeForm('fuzz-replay', replayValues);
      schedulePreview('fuzz-replay', replayValues, replayPreview);
    }
  }, status);

  const replayForm = buildForm(replayKind, replayValues, (name, value) => {
    replayValues[name] = value;
    storeForm('fuzz-replay', replayValues);
    schedulePreview('fuzz-replay', replayValues, replayPreview);
  }, status);

  const fuzzCard = card(kind.title,
    el('p', { class: 'muted small' }, kind.description),
    form,
    el('div', { class: 'form-actions' },
      el('button', { class: 'primary', onclick: () => startJob('fuzz', values) }, 'Start fuzzing'),
      el('button', { class: 'small', onclick: () => startJob('fuzz-self-test', {}) }, 'Self-test'),
      el('span', { class: 'muted small' }, kind.hint || '')),
    el('div', { class: 'label' }, 'Command'),
    preview);

  const replayCard = card('Replay a mutant',
    el('p', { class: 'muted small' }, replayKind.description),
    replayForm,
    el('div', { class: 'form-actions' },
      el('button', { onclick: () => startJob('fuzz-replay', replayValues) }, 'Replay')),
    el('div', { class: 'label' }, 'Command'),
    replayPreview);

  const seedsCard = card('Recent seeds', status.recentSeeds.length
    ? seedChips(status.recentSeeds)
    : el('p', { class: 'empty' }, 'No fuzz runs yet.'));

  view.append(el('div', { class: 'split' }, fuzzCard, el('div', { class: 'stack' }, replayCard, seedsCard)));
  schedulePreview('fuzz', values, preview);
  schedulePreview('fuzz-replay', replayValues, replayPreview);

  const history = jobs.filter(job => job.kind === 'fuzz' || job.kind === 'fuzz-replay' || job.kind === 'fuzz-self-test').slice(0, 10);
  view.append(section('Recent fuzz runs', history.length
    ? jobTable(history)
    : el('p', { class: 'empty' }, 'No fuzz runs yet.')));
}

async function renderMutation(view, query) {
  setTitle('Mutation testing', '');
  const [status, jobs, kinds, mutation] = await Promise.all([
    api('/api/status'), api('/api/jobs'), ensureKinds(), api('/api/mutation'),
  ]);
  view.replaceChildren();
  state.status = status;
  state.jobs = jobs;
  updateSidebar(status);
  setTitle('Mutation testing', status.git ? `${status.git.branch}@${status.git.commit}` : '');

  const kind = kinds.find(candidate => candidate.kind === 'mutation');
  if (!kind) {
    view.append(el('p', { class: 'error-text' }, 'The mutation job is not defined.'));
    return;
  }

  const values = storedForm('mutation', defaultValues(kind));
  applyQuery(kind, values, query);
  storeForm('mutation', values);

  const preview = el('div', { class: 'command-preview' });
  const form = buildForm(kind, values, (name, value) => {
    values[name] = value;
    storeForm('mutation', values);
    schedulePreview('mutation', values, preview);
  }, status);

  view.append(card(kind.title,
    el('p', { class: 'muted small' }, kind.description),
    status.build.stale
      ? banner('A harness is older than the sources. Turn on “Build first”, or build from the Overview, before running.')
      : null,
    form,
    el('div', { class: 'form-actions' },
      el('button', { class: 'primary', onclick: () => startJob('mutation', values) }, 'Start mutation run'),
      el('span', { class: 'muted small' }, kind.hint || '')),
    el('div', { class: 'label' }, 'Command'),
    preview));
  schedulePreview('mutation', values, preview);

  const resultFor = id => mutation.results.find(result => result.target === id);
  const rows = el('tbody');
  for (const target of mutation.targets) {
    const result = resultFor(target.id);
    rows.append(el('tr', {},
      el('td', {},
        el('div', {}, target.title),
        el('div', { class: 'muted small mono' }, target.path),
        el('div', { class: 'muted small' },
          `filters: ${((result && result.filters.length ? result.filters : target.filters) || []).join(', ') || '(all tests)'}`),
        target.arguments && target.arguments.length
          ? el('div', { class: 'muted small mono' }, `mode: ${target.arguments.join(' ')}`)
          : null,
        target.note ? el('div', { class: 'muted small' }, target.note) : null),
      el('td', {}, result
        ? el('span', { class: `pill ${scoreClass(result.score)}` }, `${result.score.toFixed(2)}%`)
        : el('span', { class: 'muted' }, 'not run')),
      el('td', { class: 'num' }, result
        ? (result.partial ? `${formatNumber(result.mutants)}/${formatNumber(result.sites)}` : formatNumber(result.mutants))
        : '—'),
      el('td', { class: 'num' }, result ? formatNumber(result.killed) : '—'),
      el('td', { class: 'num' }, result ? formatNumber(result.survived) : '—'),
      el('td', { class: 'num' }, result ? formatNumber(result.equivalent) : '—'),
      el('td', { class: 'num' }, result ? formatNumber(result.invalid) : '—'),
      el('td', { class: 'muted' }, result ? ago(result.startedAt) : '')));
  }
  view.append(section('Targets', el('div', { class: 'table-wrap' },
    el('table', { class: 'table' },
      el('thead', {}, el('tr', {},
        el('th', {}, 'Target'),
        el('th', {}, 'Score'),
        el('th', { class: 'num' }, 'Mutants'),
        el('th', { class: 'num' }, 'Killed'),
        el('th', { class: 'num' }, 'Survived'),
        el('th', { class: 'num', title: 'Sites whose mutation no program can observe' }, 'Equivalent'),
        el('th', { class: 'num' }, 'Invalid'),
        el('th', {}, 'Last run'))),
      rows))));

  const survivors = el('div', { class: 'findings-list' });
  const withSurvivors = mutation.results.filter(result => result.survived > 0);
  if (!withSurvivors.length) {
    survivors.append(el('p', { class: 'empty' },
      mutation.results.length
        ? 'No survivors in the recorded runs.'
        : 'No mutation runs yet. A survivor is a missing test or a line nothing needs.'));
  }
  for (const result of withSurvivors) {
    survivors.append(el('div', { class: 'muted small' },
      `${result.path} · ${result.survived} of ${result.mutants} mutants survived · ${ago(result.startedAt)}`));
    for (const item of result.items.filter(entry => entry.result === 'survived')) {
      survivors.append(el('div', { class: 'finding' },
        el('div', { class: 'finding-main' },
          el('div', { class: 'finding-title' }, `${item.description}`),
          el('div', { class: 'muted small mono' }, `site ${item.site} · ${result.path}:${item.line}:${item.column}`),
          el('div', { class: 'muted small mono' }, item.original)),
        el('div', { class: 'finding-side' },
          result.killed + result.survived > 0
            ? el('span', { class: 'muted small' }, `${((result.killed / (result.killed + result.survived)) * 100).toFixed(0)}% file score`)
            : null),
        el('div', { class: 'finding-actions' },
          el('button', { class: 'small', onclick: () => showSource(result.path) }, 'Show source'),
          el('button', { class: 'small', onclick: () => copy(`${result.path}:${item.line}`) }, 'Copy location'))));
    }
  }
  view.append(section('Survivors', survivors));

  const history = jobs.filter(job => job.kind === 'mutation').slice(0, 8);
  view.append(section('Recent mutation runs', history.length
    ? jobTable(history)
    : el('p', { class: 'empty' }, 'No mutation runs yet.')));
}

async function renderFindings(view) {
  setTitle('Findings', '');
  const [findings, status] = await Promise.all([api('/api/findings'), api('/api/status')]);
  view.replaceChildren();
  state.findings = findings;
  state.status = status;
  updateSidebar(status);
  setTitle('Findings', status.git ? `${status.git.branch}@${status.git.commit}` : '');

  const list = el('div', { class: 'findings-list' });
  const search = el('input', { type: 'text', placeholder: 'Filter findings' });
  search.addEventListener('input', () => {
    const term = search.value.trim().toLowerCase();
    for (const row of list.children)
      row.hidden = term.length > 0 && !(row.dataset.search || '').includes(term);
  });

  if (!findings.length) {
    list.append(el('p', { class: 'empty' }, 'No findings. The fuzzer writes failures to tests/fuzz/findings/.'));
  }

  for (const finding of findings) {
    const meta = [
      finding.seed != null ? `seed ${finding.seed}` : null,
      finding.mutant != null ? `mutant ${finding.mutant}` : null,
      finding.tier != null ? `tier ${finding.tier}` : null,
      finding.mutation,
    ].filter(Boolean).join(' · ');

    const row = el('div', { class: 'finding' },
      el('div', { class: 'finding-main' },
        el('div', { class: 'finding-title' }, finding.finding || finding.fileName),
        el('div', { class: 'muted small' }, meta || finding.path),
        finding.summary ? el('div', { class: 'muted small' }, finding.summary) : null),
      el('div', { class: 'finding-side' },
        el('span', { class: `pill ${finding.kind === 'log' ? 'dim' : 'warn'}` }, finding.kind),
        el('span', { class: 'muted small' }, ago(finding.modified))),
      el('div', { class: 'finding-actions' },
        el('button', { class: 'small', onclick: () => showFinding(finding) }, 'View'),
        el('button', { class: 'small', onclick: () => copy(finding.path) }, 'Copy path'),
        finding.seed != null && finding.mutant != null
          ? el('button', { class: 'small primary', onclick: () => { location.hash = replayHash(finding); } }, 'Replay')
          : null));
    row.dataset.search = [
      finding.finding, finding.fileName, finding.path, finding.mutation, finding.seed, finding.mutant, finding.summary,
    ].filter(value => value !== null && value !== undefined).join(' ').toLowerCase();
    list.append(row);
  }

  view.append(el('div', { class: 'row between' },
    el('span', { class: 'muted small' }, `${plural(findings.length, 'file', 'files')} in tests/fuzz/findings`),
    el('div', { class: 'row' }, search, el('button', { onclick: () => route() }, 'Refresh'))));
  view.append(el('div', { class: 'section' }, list));
}

async function renderReference(view) {
  setTitle('Reference', '');
  const groups = await api('/api/reference');
  view.replaceChildren();
  for (const group of groups) {
    view.append(section(group.title, ...group.items.map(item => el('div', { class: 'ref' },
      el('div', { class: 'ref-head' },
        el('strong', {}, item.title),
        item.href ? el('a', { class: 'btn small', href: item.href }, 'Open form') : null),
      el('p', { class: 'muted small' }, item.description),
      el('div', { class: 'command-row' },
        el('code', {}, item.command),
        el('button', { class: 'small', onclick: () => copy(item.command) }, 'Copy')),
      item.note ? el('p', { class: 'note small' }, item.note) : null))));
  }
}

async function renderJob(view, id) {
  setTitle('Job', id || '');
  if (!id) {
    view.append(el('p', { class: 'error-text' }, 'No job id.'));
    return;
  }

  let job;
  try {
    job = await api(`/api/jobs/${encodeURIComponent(id)}`);
  } catch (error) {
    view.replaceChildren(el('p', { class: 'error-text' }, error.message));
    return;
  }

  view.replaceChildren();
  state.job = job;
  setTitle(job.title, `${job.kind} · ${id}`);

  const statusHost = el('div', { class: 'row' });
  const metricHost = el('div', { class: 'metrics' });
  const failureHost = el('div', { class: 'failures' });
  const findingHost = el('div', { class: 'failures' });
  const logHost = el('div', { class: 'log' });
  const follow = el('input', { type: 'checkbox', checked: state.autoScroll });
  follow.addEventListener('change', () => {
    state.autoScroll = follow.checked;
    if (follow.checked && state.logElement) state.logElement.scrollTop = state.logElement.scrollHeight;
  });

  const actions = el('div', { class: 'actions' },
    el('button', { id: 'cancelButton', class: 'danger small', onclick: () => cancelJob(id) }, 'Cancel'),
    el('button', { class: 'small', onclick: () => startJob(job.kind, job.parameters) }, 'Run again'),
    job.commands.length ? el('button', { class: 'small', onclick: () => copy(job.commands.join('\n')) }, 'Copy command') : null,
    el('a', { class: 'btn small', href: jobFormHref(job.kind) }, 'Open form'));

  const commands = el('details', { class: 'card' },
    el('summary', { class: 'label' }, 'Commands'),
    ...job.commands.map(command => el('div', { class: 'command-row' },
      el('code', {}, `$ ${command}`),
      el('button', { class: 'small', onclick: () => copy(command) }, 'Copy'))));

  view.append(el('div', { class: 'stack' },
    el('div', { class: 'row between' }, statusHost, actions),
    metricHost,
    failureHost,
    findingHost,
    el('div', {},
      el('div', { class: 'log-toolbar' },
        el('span', { class: 'label' }, 'Output'),
        el('label', { class: 'check-inline' }, follow, 'Follow output')),
      logHost),
    commands));

  state.logElement = logHost;
  state.logLines = [];

  const lineClass = text => {
    if (text.startsWith('$ ')) return 'cmd';
    if (/^# succeeded\b/.test(text)) return 'ok';
    if (/^# findings\b/.test(text)) return 'warn';
    if (/^# (failed|interrupted)\b/.test(text)) return 'bad';
    if (text.startsWith('# ')) return 'dim';
    if (/^\s*(FAIL|FINDING)\b/.test(text) || /^Build FAILED/i.test(text)) return 'bad';
    if (/\] SURVIVED /.test(text) || /^survivors:$/.test(text)) return 'warn';
    if (/^\s*\d+\s+(Error|Warning)\(s\)/i.test(text)) return 'dim';
    if (/\berror\b/i.test(text)) return 'bad';
    if (/\bwarning\b/i.test(text)) return 'warn';
    if (/^Build succeeded/i.test(text) || /^\s*\d+ passed, 0 failed/.test(text)) return 'ok';
    return '';
  };

  const logLine = text => {
    const kindName = lineClass(text);
    return el('div', { class: 'log-line' + (kindName ? ' ' + kindName : '') }, text);
  };

  const appendLine = text => {
    const log = state.logElement;
    if (!log) return;
    const placeholder = log.querySelector('.log-empty');
    if (placeholder) placeholder.remove();
    state.logLines.push(text);
    if (state.logLines.length > 5000) state.logLines.shift();
    log.append(logLine(text));
    if (log.childElementCount > 5000 && log.firstElementChild) log.firstElementChild.remove();
    if (state.autoScroll) log.scrollTop = log.scrollHeight;
  };

  const renderLog = lines => {
    const log = state.logElement;
    if (!log) return;
    state.logLines = lines.slice(-5000);
    const fragment = document.createDocumentFragment();
    for (const text of state.logLines) fragment.append(logLine(text));
    log.replaceChildren(fragment);
    if (!state.logLines.length) log.append(el('div', { class: 'log-empty' }, 'Waiting for output…'));
    if (state.autoScroll) log.scrollTop = log.scrollHeight;
  };

  const updateHeader = current => {
    state.job = current;
    const parts = [statePill(current.state)];
    if (current.exitCode !== null && current.exitCode !== undefined)
      parts.push(el('span', { class: 'muted small' }, `exit ${current.exitCode}`));
    if (current.error)
      parts.push(el('span', { class: 'error-text small' }, current.error));
    parts.push(el('span', { id: 'jobElapsed', class: 'muted small' }));
    statusHost.replaceChildren(...parts);
    const cancel = document.getElementById('cancelButton');
    if (cancel) cancel.disabled = !current.canCancel;
    updateElapsed();
  };

  const updateMetrics = metrics => {
    const tiles = [];
    if (metrics.passed !== null && metrics.passed !== undefined) tiles.push(metric('Passed', metrics.passed));
    if (metrics.failed !== null && metrics.failed !== undefined) tiles.push(metric('Failed', metrics.failed));
    if (metrics.mutants !== null && metrics.mutants !== undefined) tiles.push(metric('Mutants', metrics.mutants));
    if (metrics.compiled !== null && metrics.compiled !== undefined) tiles.push(metric('Compiled', metrics.compiled));
    if (metrics.findings !== null && metrics.findings !== undefined) tiles.push(metric('Findings', metrics.findings));
    if (metrics.killed !== null && metrics.killed !== undefined) tiles.push(metric('Killed', metrics.killed));
    if (metrics.survived !== null && metrics.survived !== undefined) tiles.push(metric('Survived', metrics.survived));
    if (metrics.equivalent !== null && metrics.equivalent !== undefined && metrics.equivalent > 0) tiles.push(metric('Equivalent', metrics.equivalent));
    if (metrics.invalid !== null && metrics.invalid !== undefined) tiles.push(metric('Invalid', metrics.invalid));
    if (metrics.timedOut !== null && metrics.timedOut !== undefined && metrics.timedOut > 0) tiles.push(metric('Timeout', metrics.timedOut));
    if (metrics.score !== null && metrics.score !== undefined) tiles.push(metric('Score', `${metrics.score.toFixed(2)}%`));
    if (metrics.rate) tiles.push(metric('Rate', metrics.rate));
    if (metrics.seconds !== null && metrics.seconds !== undefined) tiles.push(metric('Seconds', metrics.seconds.toFixed(1)));
    metricHost.replaceChildren(...tiles);

    failureHost.replaceChildren();
    if (metrics.failures && metrics.failures.length) {
      failureHost.append(el('div', { class: 'label' }, 'Failures'));
      for (const name of metrics.failures) {
        failureHost.append(el('div', { class: 'failure-row' },
          el('button', { class: 'link', onclick: () => showCase(name) }, name),
          el('button', { class: 'link subtle', onclick: () => showOutput(name, 'cpp') }, 'C++'),
          el('button', { class: 'link subtle', onclick: () => showOutput(name, 'ir') }, 'IR')));
      }
    }

    findingHost.replaceChildren();
    if (metrics.findingKeys && metrics.findingKeys.length) {
      findingHost.append(el('div', { class: 'label' }, 'Findings in this run'));
      for (const key of metrics.findingKeys) findingHost.append(el('div', { class: 'mono small' }, key));
      findingHost.append(el('a', { class: 'small', href: '#/findings' }, 'Open findings'));
    }
  };

  const updateElapsed = () => {
    const target = document.getElementById('jobElapsed');
    if (!target || !state.job) return;
    if (state.job.state === 'queued') {
      target.textContent = 'queued';
      return;
    }
    target.textContent = state.job.startedAt
      ? duration(state.job) + (state.job.endedAt ? '' : ' · running')
      : '';
  };

  const done = () => {
    refreshStatus();
  };

  state.jobView = { updateHeader, updateMetrics, done };
  state.logView = { appendLine, renderLog };

  updateHeader(job);
  updateMetrics(job.metrics);
  renderLog([]);
  state.tick = setInterval(updateElapsed, 1000);
  openStream(id);
}

function jobFormHref(kind) {
  if (kind.startsWith('fuzz')) return '#/fuzz';
  if (kind === 'suite') return '#/tests';
  if (kind === 'mutation') return '#/mutation';
  return '#/overview';
}

function openStream(id) {
  const source = new EventSource(`/api/jobs/${encodeURIComponent(id)}/stream`);
  state.stream = source;

  source.addEventListener('snapshot', event => {
    if (!state.logView || !state.jobView) return;
    const data = JSON.parse(event.data);
    state.logView.renderLog(data.lines.map(line => line.text));
    state.jobView.updateHeader(data.job);
    state.jobView.updateMetrics(data.job.metrics);
    if (!['queued', 'running'].includes(data.job.state)) {
      state.jobView.done(data.job);
      source.close();
      if (state.stream === source) state.stream = null;
    }
  });

  source.addEventListener('line', event => {
    if (!state.logView || !state.jobView) return;
    const data = JSON.parse(event.data);
    state.logView.appendLine(data.text);
    state.jobView.updateMetrics(data.metrics);
  });

  source.addEventListener('done', event => {
    if (!state.logView || !state.jobView) return;
    const data = JSON.parse(event.data);
    state.jobView.updateHeader(data.job);
    state.jobView.updateMetrics(data.job.metrics);
    state.jobView.done(data.job);
    source.close();
    if (state.stream === source) state.stream = null;
  });
}

function replayHash(finding) {
  const query = new URLSearchParams({
    seed: String(finding.seed),
    tier: String(finding.tier ?? 2),
    index: String(finding.mutant),
  });
  return `#/fuzz?${query.toString()}`;
}

function showDialog(title, text, copyText) {
  document.getElementById('dialogTitle').textContent = title;
  document.getElementById('dialogBody').textContent = text;
  dialog.dataset.copy = copyText || '';
  if (!dialog.open) dialog.showModal();
}

async function showFinding(finding) {
  try {
    const text = await api(`/api/findings/content?path=${encodeURIComponent(finding.path)}`);
    showDialog(finding.finding || finding.fileName, text, finding.path);
  } catch (error) {
    toast(error.message, true);
  }
}

async function showSource(path) {
  try {
    const text = await api(`/api/file?path=${encodeURIComponent(path)}`);
    showDialog(path, text, path);
  } catch (error) {
    toast(error.message, true);
  }
}

async function showCase(name) {
  const path = `tests/cases/${name}.wyl`;
  try {
    const text = await api(`/api/file?path=${encodeURIComponent(path)}`);
    showDialog(path, text, path);
  } catch (error) {
    toast(error.message, true);
  }
}

async function showOutput(name, extension) {
  const leaf = name.split('/').pop();
  const path = `tests/.out/${name}/${leaf}.${extension}`;
  try {
    const text = await api(`/api/file?path=${encodeURIComponent(path)}`);
    showDialog(path, text, path);
  } catch (error) {
    toast(error.message, true);
  }
}

async function init() {
  document.getElementById('dialogClose').addEventListener('click', () => dialog.close());
  document.getElementById('dialogCopy').addEventListener('click', () => {
    if (dialog.dataset.copy) copy(dialog.dataset.copy);
  });
  dialog.addEventListener('click', event => {
    if (event.target === dialog) dialog.close();
  });

  window.addEventListener('hashchange', route);
  await route();
  await refreshStatus();
  setInterval(refreshStatus, 15000);
}

init();
