/**
 * 管理页面：单页 HTML，原生 JS，无构建步骤。
 *
 * 页面只读展示与表单操作；渲染一律走 textContent，不拼 innerHTML——上游名与
 * 工具名都来自外部，拼字符串就是等着被注入。
 */
export const MANAGEMENT_PAGE_HTML = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>MCP Proxy</title>
<style>
  body { font-family: system-ui, sans-serif; margin: 1.5rem; color: #222; }
  h1 { font-size: 1.2rem; }
  table { border-collapse: collapse; width: 100%; margin-bottom: 1.5rem; }
  th, td { border: 1px solid #ccc; padding: 0.4rem 0.6rem; text-align: left; font-size: 0.9rem; }
  th { background: #f3f3f3; }
  .status-running { color: #0a7a28; }
  .status-failed { color: #b00020; }
  .status-stopped { color: #666; }
  .status-starting { color: #a06000; }
  form { display: grid; gap: 0.5rem; max-width: 40rem; }
  label { display: grid; gap: 0.2rem; font-size: 0.9rem; }
  input, textarea { font: inherit; padding: 0.3rem; }
  button { font: inherit; padding: 0.3rem 0.8rem; cursor: pointer; }
  .row { display: flex; gap: 0.4rem; }
  pre { background: #f6f6f6; padding: 0.6rem; overflow: auto; max-height: 16rem; font-size: 0.8rem; }
  .muted { color: #666; font-size: 0.85rem; }
</style>
</head>
<body>
<h1>MCP Proxy</h1>
<p class="muted" id="summary">Loading…</p>

<table>
  <thead>
    <tr><th>ID</th><th>State</th><th>Tools</th><th>Last error</th><th>Actions</th></tr>
  </thead>
  <tbody id="servers"></tbody>
</table>

<h2>Diagnostics</h2>
<pre id="diagnostics"></pre>

<h2 id="form-title">Add upstream</h2>
<form id="form">
  <label>ID <input name="id" required pattern="[a-z0-9][a-z0-9_-]*" /></label>
  <label>Command <input name="command" required /></label>
  <label>Args (one per line) <textarea name="args" rows="3"></textarea></label>
  <label>Env (KEY=VALUE per line) <textarea name="env" rows="3"></textarea></label>
  <label>Working directory <input name="cwd" /></label>
  <label>Enabled <input name="enabled" type="checkbox" checked /></label>
  <div class="row">
    <button type="submit">Save</button>
    <button type="button" id="reset">New</button>
  </div>
</form>

<script>
(function () {
  const token = new URLSearchParams(window.location.search).get('token') || '';
  const serversBody = document.getElementById('servers');
  const summary = document.getElementById('summary');
  const diagnostics = document.getElementById('diagnostics');
  const form = document.getElementById('form');
  const formTitle = document.getElementById('form-title');

  function headers() {
    return { 'content-type': 'application/json', 'x-proxy-token': token };
  }

  function cell(row, text, className) {
    const td = document.createElement('td');
    td.textContent = text === undefined || text === null ? '' : String(text);
    if (className) { td.className = className; }
    row.appendChild(td);
    return td;
  }

  function button(label, action) {
    const b = document.createElement('button');
    b.textContent = label;
    b.type = 'button';
    b.addEventListener('click', action);
    return b;
  }

  async function call(method, path, body) {
    const options = { method: method, headers: headers() };
    if (body !== undefined) { options.body = JSON.stringify(body); }
    const response = await fetch(path, options);
    const text = await response.text();
    let parsed = null;
    try { parsed = text ? JSON.parse(text) : null; } catch (error) { parsed = { error: text }; }
    if (!response.ok) {
      throw new Error((parsed && parsed.error) || ('HTTP ' + response.status));
    }
    return parsed;
  }

  async function refresh() {
    try {
      const data = await call('GET', '/api/servers');
      serversBody.textContent = '';
      const servers = (data && data.servers) || [];
      for (const server of servers) {
        const row = document.createElement('tr');
        cell(row, server.id);
        cell(row, server.status, 'status-' + server.status);
        cell(row, server.toolCount);
        cell(row, server.lastError || '');
        const actions = document.createElement('td');
        actions.appendChild(button('Start', () => act('start', server.id)));
        actions.appendChild(button('Stop', () => act('stop', server.id)));
        actions.appendChild(button('Restart', () => act('restart', server.id)));
        actions.appendChild(button('Diagnostics', () => showDiagnostics(server.id)));
        actions.appendChild(button('Delete', () => act('delete', server.id)));
        actions.appendChild(button('Edit', () => loadForEdit(server)));
        row.appendChild(actions);
        serversBody.appendChild(row);
      }
      summary.textContent = servers.length + ' upstream(s) configured';
    } catch (error) {
      summary.textContent = 'Failed to load: ' + error.message;
    }
  }

  async function act(action, id) {
    try {
      if (action === 'delete') {
        await call('DELETE', '/api/servers/' + encodeURIComponent(id));
      } else {
        await call('POST', '/api/servers/' + encodeURIComponent(id) + '/' + action);
      }
      await refresh();
    } catch (error) {
      summary.textContent = 'Action failed: ' + error.message;
    }
  }

  async function showDiagnostics(id) {
    try {
      const data = await call('GET', '/api/servers/' + encodeURIComponent(id) + '/diagnostics');
      diagnostics.textContent = ((data && data.diagnostics) || []).join('\\n');
    } catch (error) {
      diagnostics.textContent = 'Failed to load diagnostics: ' + error.message;
    }
  }

  function loadForEdit(server) {
    const entry = (server && server.entry) || {};
    form.elements['id'].value = entry.id || '';
    form.elements['id'].disabled = true;
    form.elements['command'].value = entry.command || '';
    form.elements['args'].value = ((entry.args || []).join('\\n'));
    form.elements['env'].value = Object.entries(entry.env || {}).map(([k, v]) => k + '=' + v).join('\\n');
    form.elements['cwd'].value = entry.cwd || '';
    form.elements['enabled'].checked = entry.enabled !== false;
    formTitle.textContent = 'Edit upstream ' + entry.id;
    form.dataset['mode'] = 'edit';
  }

  document.getElementById('reset').addEventListener('click', function () {
    form.reset();
    form.elements['id'].disabled = false;
    form.dataset['mode'] = 'add';
    formTitle.textContent = 'Add upstream';
  });

  form.addEventListener('submit', async function (event) {
    event.preventDefault();
    const env = {};
    for (const line of form.elements['env'].value.split('\\n')) {
      const trimmed = line.trim();
      if (!trimmed) { continue; }
      const index = trimmed.indexOf('=');
      if (index <= 0) { continue; }
      env[trimmed.slice(0, index)] = trimmed.slice(index + 1);
    }
    const body = {
      id: form.elements['id'].value.trim(),
      command: form.elements['command'].value.trim(),
      args: form.elements['args'].value.split('\\n').map((s) => s.trim()).filter(Boolean),
      env: env,
      cwd: form.elements['cwd'].value.trim() || undefined,
      enabled: form.elements['enabled'].checked,
    };
    try {
      if (form.dataset['mode'] === 'edit') {
        await call('PUT', '/api/servers/' + encodeURIComponent(body.id), body);
      } else {
        await call('POST', '/api/servers', body);
      }
      form.reset();
      form.elements['id'].disabled = false;
      form.dataset['mode'] = 'add';
      formTitle.textContent = 'Add upstream';
      await refresh();
    } catch (error) {
      summary.textContent = 'Save failed: ' + error.message;
    }
  });

  refresh();
  setInterval(refresh, 2000);
})();
</script>
</body>
</html>
`;
