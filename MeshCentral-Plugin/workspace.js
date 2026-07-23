'use strict';

const createModule = require('./module.js').createModule;

module.exports.workspace = function workspacePlugin(parent) {
    const obj = {};
    const pluginRoot = parent.path.join(parent.pluginPath, 'workspace');
    const pluginModule = createModule(parent);
    const assets = {
        'main.js': { path: parent.path.join(pluginRoot, 'public', 'main.js'), type: 'text/javascript; charset=utf-8' },
        'main.css': { path: parent.path.join(pluginRoot, 'public', 'main.css'), type: 'text/css; charset=utf-8' }
    };

    obj.parent = parent;
    obj.meshServer = parent.parent;
    obj.exports = ['onWebUIStartupEnd', 'onDeviceRefreshEnd', 'goPageStart', 'goPageEnd'];

    function send(res, code, type, body) { res.statusCode = code; res.setHeader('Content-Type', type); res.setHeader('Cache-Control', 'no-store'); res.end(body); }
    function sendJson(res, code, value) { send(res, code, 'application/json; charset=utf-8', JSON.stringify(value)); }
    function handlePromise(res, work) { Promise.resolve(work).then(function (value) { sendJson(res, 200, { ok: true, result: value }); }).catch(function (error) { sendJson(res, 400, { ok: false, error: String(error && error.message || error || 'Request failed.') }); }); }

    obj.server_startup = function () { console.log('[MeshCentral-Workspace] Plugin 0.8.6 loaded'); };

    obj.onWebUIStartupEnd = function () {
        if (typeof window === 'undefined' || typeof document === 'undefined') return;
        window.MeshCentralWorkspace = window.MeshCentralWorkspace || {};
        window.MeshCentralWorkspace.bootstrapPromise = null;
        var endpoint = function (asset) { var url = new URL('pluginadmin.ashx', window.location.href); url.searchParams.set('pin', 'workspace'); url.searchParams.set('asset', asset); url.searchParams.set('v', '0.8.6'); return url.href; };
        var load = function (id, source) { return new Promise(function (resolve, reject) { var existing = document.getElementById(id); if (existing) existing.remove(); var script = document.createElement('script'); script.id = id; script.src = source; script.async = false; script.onload = function () { script.setAttribute('data-loaded', '1'); resolve(); }; script.onerror = reject; (document.head || document.documentElement).appendChild(script); }); };
        var oldStyle = document.getElementById('workspace-plugin-css'); if (oldStyle) oldStyle.remove();
        var style = document.createElement('link'); style.id = 'workspace-plugin-css'; style.rel = 'stylesheet'; style.href = endpoint('main.css'); (document.head || document.documentElement).appendChild(style);
        window.MeshCentralWorkspace.bootstrapPromise = load('workspace-main-script', endpoint('main.js')).then(function () { return window.MeshCentralWorkspace.initialize(); }).catch(function (error) { window.MeshCentralWorkspace.bootstrapPromise = null; if (window.console) console.error('Workspace bootstrap error', error); });
    };

    obj.onDeviceRefreshEnd = function (nodeId) { if (typeof window === 'undefined') return; window.MeshCentralWorkspacePendingNodeId = nodeId; if (window.MeshCentralWorkspace && typeof window.MeshCentralWorkspace.onDeviceRefreshEnd === 'function') window.MeshCentralWorkspace.onDeviceRefreshEnd(nodeId); };
    obj.goPageStart = function () {};
    obj.goPageEnd = function (view) { if (typeof window !== 'undefined' && window.MeshCentralWorkspace && typeof window.MeshCentralWorkspace.onNativePageEnd === 'function') window.MeshCentralWorkspace.onNativePageEnd(view); };
    obj.hook_processAgentData = function (command, agent) { pluginModule.captureAgentData(command, agent); };

    obj.handleAdminReq = function (req, res, user) {
        const asset = String(req && req.query && req.query.asset || '');
        if (asset === 'status') {
            const session = pluginModule.status(user, req && req.query && req.query.id);
            if (!session) { sendJson(res, 404, { ok: false, error: 'Session not found.' }); return; }
            sendJson(res, 200, { ok: true, result: session }); return;
        }
        if (asset === 'slots') {
            sendJson(res, 200, { ok: true, result: pluginModule.list(String(req && req.query && req.query.nodeId || '')) }); return;
        }
        const file = assets[asset];
        if (!file) { send(res, 404, 'text/plain; charset=utf-8', 'Not found'); return; }
        parent.fs.readFile(file.path, function (error, data) { if (error) send(res, 404, 'text/plain; charset=utf-8', 'Not found'); else send(res, 200, file.type, data); });
    };

    obj.handleAdminPostReq = function (req, res, user) {
        const asset = String(req && req.query && req.query.asset || '');
        const body = req && req.body || {};
        if (asset === 'start') { handlePromise(res, pluginModule.start(user, body.nodeId, body.slot)); return; }
        if (asset === 'stop') { handlePromise(res, pluginModule.stop(user, body.id)); return; }
        sendJson(res, 400, { ok: false, error: 'Unknown action.' });
    };

    return obj;
};