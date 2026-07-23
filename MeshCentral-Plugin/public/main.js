(function () {
    'use strict';

    const state = { nodeId: null, sessionId: null, timer: null };

    function currentNodeId() {
        try {
            if (window.currentNode && window.currentNode._id) return window.currentNode._id;
            if (window.nodeid) return window.nodeid;
            const q = new URLSearchParams(location.search);
            return q.get('nodeid') || q.get('node') || null;
        } catch (_) { return null; }
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    }

    function render(session) {
        const root = document.getElementById('workspace-new-root');
        if (!root) return;
        const s = session || {};
        root.innerHTML = `
            <div class="workspace-card">
                <div class="workspace-toolbar">
                    <button id="workspace-connect" class="btn btn-success btn-sm">Polacz PoC</button>
                    <button id="workspace-disconnect" class="btn btn-danger btn-sm" ${state.sessionId ? '' : 'disabled'}>Rozlacz</button>
                </div>
                <h3>Workspace</h3>
                <dl class="workspace-grid">
                    <dt>Host</dt><dd>${escapeHtml(state.nodeId || '-')}</dd>
                    <dt>Stan</dt><dd>${escapeHtml(s.state || 'idle')}</dd>
                    <dt>Session ID</dt><dd>${escapeHtml(s.id || '-')}</dd>
                    <dt>PID</dt><dd>${escapeHtml(s.pid || '-')}</dd>
                    <dt>Windows Session</dt><dd>${escapeHtml(s.windowsSessionId ?? '-')}</dd>
                    <dt>User</dt><dd>${escapeHtml(s.user || '-')}</dd>
                    <dt>Desktop</dt><dd>${escapeHtml(s.desktop || '-')}</dd>
                    <dt>Version</dt><dd>${escapeHtml(s.version || '-')}</dd>
                    <dt>Heartbeat</dt><dd>${escapeHtml(s.lastHeartbeat || '-')}</dd>
                    <dt>Error</dt><dd>${escapeHtml(s.error || '-')}</dd>
                </dl>
            </div>`;
        document.getElementById('workspace-connect').onclick = start;
        document.getElementById('workspace-disconnect').onclick = stop;
    }

    async function request(path, options) {
        const response = await fetch(path, Object.assign({
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json' }
        }, options || {}));
        const data = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(data.error || `HTTP ${response.status}`);
        return data;
    }

    async function start() {
        try {
            state.nodeId = currentNodeId();
            if (!state.nodeId) throw new Error('Nie znaleziono nodeId');
            const session = await request('/workspace/api/session/start', {
                method: 'POST', body: JSON.stringify({ nodeId: state.nodeId })
            });
            state.sessionId = session.id;
            render(session);
            poll();
        } catch (e) { render({ state: 'error', error: e.message }); }
    }

    async function stop() {
        if (!state.sessionId) return;
        try {
            const session = await request(`/workspace/api/session/${encodeURIComponent(state.sessionId)}/stop`, { method: 'POST' });
            clearInterval(state.timer); state.timer = null; state.sessionId = null; render(session);
        } catch (e) { render({ state: 'error', error: e.message }); }
    }

    function poll() {
        clearInterval(state.timer);
        state.timer = setInterval(async () => {
            if (!state.sessionId) return;
            try { render(await request(`/workspace/api/session/${encodeURIComponent(state.sessionId)}`)); }
            catch (e) { render({ state: 'error', error: e.message }); }
        }, 2000);
    }

    function ensureTab() {
        state.nodeId = currentNodeId();
        if (document.getElementById('workspace-new-root')) return;
        const tabs = document.querySelector('[role="tablist"], #p11tabs, .device-tabs');
        const content = document.querySelector('.tab-content, #p11, #deviceDetails') || document.body;
        if (!tabs) return;
        const button = document.createElement('button');
        button.type = 'button'; button.className = 'workspace-tab-button'; button.textContent = 'Pulpit -New';
        const panel = document.createElement('div'); panel.id = 'workspace-new-root'; panel.className = 'workspace-panel'; panel.style.display = 'none';
        button.onclick = () => { panel.style.display = 'block'; render(); };
        tabs.appendChild(button); content.appendChild(panel);
    }

    window.MeshCentralWorkspace = { refresh: ensureTab, start, stop };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', ensureTab); else ensureTab();
})();
