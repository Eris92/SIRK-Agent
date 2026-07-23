(function () {
    'use strict';

    var state = { nodeId: '', sessionId: '', timer: null };

    function assetUrl(asset, extra) {
        var url = new URL('pluginadmin.ashx', window.location.href);
        url.searchParams.set('pin', 'workspace');
        url.searchParams.set('asset', asset);
        if (extra) Object.keys(extra).forEach(function (key) { url.searchParams.set(key, extra[key]); });
        return url.href;
    }

    function currentNodeId(explicit) {
        if (explicit) return explicit;
        if (window.MeshCentralWorkspacePendingNodeId) return window.MeshCentralWorkspacePendingNodeId;
        if (window.currentNode && window.currentNode._id) return window.currentNode._id;
        if (window.nodeid) return window.nodeid;
        try { var q = new URLSearchParams(location.search); return q.get('nodeid') || q.get('node') || ''; }
        catch (_) { return ''; }
    }

    function escapeHtml(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    async function request(asset, options, extra) {
        var response = await fetch(assetUrl(asset, extra), Object.assign({ credentials: 'same-origin' }, options || {}));
        var data = await response.json().catch(function () { return {}; });
        if (!response.ok || data.ok === false) throw new Error(data.error || ('HTTP ' + response.status));
        return data.result;
    }

    function render(session) {
        var root = document.getElementById('workspace-new-root');
        if (!root) return;
        var s = session || {};
        var busy = s.state === 'requested' || s.state === 'deploying';
        root.innerHTML = '<div class="workspace-card">' +
            '<div class="workspace-toolbar">' +
            '<button id="workspace-connect" class="btn btn-success btn-sm"' + (busy ? ' disabled' : '') + '>Polacz</button>' +
            '<button id="workspace-disconnect" class="btn btn-danger btn-sm"' + (state.sessionId ? '' : ' disabled') + '>Rozlacz</button>' +
            '</div><h3>WorkspaceHost</h3><dl class="workspace-grid">' +
            '<dt>Host</dt><dd>' + escapeHtml(state.nodeId || '-') + '</dd>' +
            '<dt>Stan</dt><dd>' + escapeHtml(s.state || 'idle') + '</dd>' +
            '<dt>Session ID</dt><dd>' + escapeHtml(s.id || state.sessionId || '-') + '</dd>' +
            '<dt>PID</dt><dd>' + escapeHtml(s.pid || '-') + '</dd>' +
            '<dt>User</dt><dd>' + escapeHtml(s.user || '-') + '</dd>' +
            '<dt>Desktop</dt><dd>' + escapeHtml(s.desktop || '-') + '</dd>' +
            '<dt>Version</dt><dd>' + escapeHtml(s.version || '-') + '</dd>' +
            '<dt>Ostatni status</dt><dd>' + escapeHtml(s.lastHeartbeat || s.updatedAt || '-') + '</dd>' +
            '<dt>Blad</dt><dd>' + escapeHtml(s.error || '-') + '</dd>' +
            '</dl><p class="workspace-note">Etap 0.3: plugin wysyla przez MeshAgenta polecenie pobrania, weryfikacji SHA256 i uruchomienia WorkspaceHost w sesji interaktywnego uzytkownika.</p></div>';
        document.getElementById('workspace-connect').onclick = start;
        document.getElementById('workspace-disconnect').onclick = stop;
    }

    async function start() {
        try {
            state.nodeId = currentNodeId();
            if (!state.nodeId) throw new Error('Nie znaleziono nodeId.');
            render({ state: 'requested' });
            var body = new URLSearchParams();
            body.set('nodeId', state.nodeId);
            var session = await request('start', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
                body: body.toString()
            });
            state.sessionId = session.id;
            render(session);
            poll();
        } catch (error) { render({ state: 'error', error: error.message }); }
    }

    async function stop() {
        if (!state.sessionId) return;
        try {
            var body = new URLSearchParams();
            body.set('id', state.sessionId);
            var session = await request('stop', {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
                body: body.toString()
            });
            if (state.timer) clearInterval(state.timer);
            state.timer = null;
            state.sessionId = '';
            render(session);
        } catch (error) { render({ state: 'error', error: error.message }); }
    }

    function poll() {
        if (state.timer) clearInterval(state.timer);
        state.timer = setInterval(async function () {
            if (!state.sessionId) return;
            try {
                var session = await request('status', null, { id: state.sessionId });
                render(session);
                if (session.state === 'running' || session.state === 'error' || session.state === 'stopped') {
                    clearInterval(state.timer);
                    state.timer = null;
                }
            } catch (error) { render({ state: 'error', error: error.message }); }
        }, 1500);
    }

    function ensureTab(nodeId) {
        state.nodeId = currentNodeId(nodeId);
        if (document.getElementById('workspace-new-root')) return;
        var tabs = document.querySelector('[role="tablist"], #p11tabs, .device-tabs');
        var content = document.querySelector('.tab-content, #p11, #deviceDetails') || document.body;
        if (!tabs) return;
        var button = document.createElement('button');
        button.type = 'button';
        button.className = 'workspace-tab-button';
        button.textContent = 'Pulpit -New';
        var panel = document.createElement('div');
        panel.id = 'workspace-new-root';
        panel.className = 'workspace-panel';
        panel.style.display = 'none';
        button.onclick = function () { panel.style.display = 'block'; render(); };
        tabs.appendChild(button);
        content.appendChild(panel);
    }

    window.MeshCentralWorkspace = { refresh: ensureTab, start: start, stop: stop };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', function () { ensureTab(); });
    else ensureTab();
})();
