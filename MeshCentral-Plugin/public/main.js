"use strict";

(function () {
    window.MeshCentralWorkspace = window.MeshCentralWorkspace || {};
    var plugin = window.MeshCentralWorkspace;
    plugin.state = plugin.state || { nodeId: "", sessionId: "", timer: null };

    function assetUrl(asset, extra) {
        var url = new URL("pluginadmin.ashx", window.location.href);
        url.searchParams.set("pin", "workspace");
        url.searchParams.set("asset", asset);
        url.searchParams.set("v", "0.4.0");
        if (extra) Object.keys(extra).forEach(function (key) { url.searchParams.set(key, extra[key]); });
        return url.href;
    }

    function request(asset, options, extra) {
        return fetch(assetUrl(asset, extra), Object.assign({ credentials: "same-origin", cache: "no-store" }, options || {})).then(function (response) {
            return response.text().then(function (text) {
                var data = {};
                try { data = JSON.parse(text || "{}"); } catch (error) { data = { ok: false, error: text || response.statusText }; }
                if (!response.ok || data.ok === false) throw new Error(data.error || response.statusText || "Request failed");
                return data.result;
            });
        });
    }

    function post(asset, values) {
        var body = new URLSearchParams();
        Object.keys(values || {}).forEach(function (key) { body.set(key, values[key]); });
        return request(asset, { method: "POST", headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" }, body: body.toString() });
    }

    function escapeHtml(value) {
        return String(value == null ? "" : value).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]; });
    }

    function mark(element, label) {
        if (!element) return element;
        element.setAttribute("data-meshcentral-plugin-pin", "workspace");
        element.setAttribute("data-meshcentral-plugin-click", label || element.id || "Pulpit -New");
        return element;
    }

    function render(session) {
        var root = document.getElementById("workspace-device-page");
        if (!root) return;
        var s = session || {};
        var busy = ["requested", "deploying", "stopping"].indexOf(s.state) >= 0;
        root.className = "workspace-panel";
        root.innerHTML = '<div class="workspace-card"><div class="workspace-toolbar">' +
            '<button id="workspace-connect" class="btn btn-success btn-sm"' + (busy ? ' disabled' : '') + '>Polacz</button>' +
            '<button id="workspace-disconnect" class="btn btn-danger btn-sm"' + (!plugin.state.sessionId || busy ? ' disabled' : '') + '>Rozlacz</button>' +
            '</div><h3>WorkspaceHost</h3><dl class="workspace-grid">' +
            '<dt>Host</dt><dd>' + escapeHtml(plugin.state.nodeId || '-') + '</dd>' +
            '<dt>Stan</dt><dd>' + escapeHtml(s.state || 'idle') + '</dd>' +
            '<dt>Session ID</dt><dd>' + escapeHtml(s.id || plugin.state.sessionId || '-') + '</dd>' +
            '<dt>PID</dt><dd>' + escapeHtml(s.pid || '-') + '</dd>' +
            '<dt>Windows Session</dt><dd>' + escapeHtml(s.windowsSessionId == null ? '-' : s.windowsSessionId) + '</dd>' +
            '<dt>User</dt><dd>' + escapeHtml(s.user || '-') + '</dd>' +
            '<dt>Desktop</dt><dd>' + escapeHtml(s.desktop || '-') + '</dd>' +
            '<dt>Version</dt><dd>' + escapeHtml(s.version || '-') + '</dd>' +
            '<dt>Uptime</dt><dd>' + escapeHtml(s.uptimeSeconds == null ? '-' : s.uptimeSeconds + ' s') + '</dd>' +
            '<dt>Ostatni heartbeat</dt><dd>' + escapeHtml(s.lastHeartbeat || '-') + '</dd>' +
            '<dt>Blad</dt><dd>' + escapeHtml(s.error || '-') + '</dd></dl></div>';
        document.getElementById("workspace-connect").onclick = start;
        document.getElementById("workspace-disconnect").onclick = stop;
    }

    function start() {
        if (!plugin.state.nodeId) { render({ state: "error", error: "Nie znaleziono nodeId." }); return; }
        render({ state: "requested" });
        post("start", { nodeId: plugin.state.nodeId }).then(function (session) {
            plugin.state.sessionId = session.id;
            render(session);
            poll();
        }).catch(function (error) { render({ state: "error", error: error.message }); });
    }

    function stop() {
        if (!plugin.state.sessionId) return;
        render({ id: plugin.state.sessionId, state: "stopping" });
        post("stop", { id: plugin.state.sessionId }).then(function (session) {
            render(session);
            poll();
        }).catch(function (error) { render({ state: "error", error: error.message }); });
    }

    function poll() {
        if (plugin.state.timer) clearInterval(plugin.state.timer);
        var run = function () {
            if (!plugin.state.sessionId) return;
            request("status", null, { id: plugin.state.sessionId }).then(function (session) {
                render(session);
                if (["error", "stopped"].indexOf(session.state) >= 0) {
                    clearInterval(plugin.state.timer); plugin.state.timer = null;
                    if (session.state === "stopped") plugin.state.sessionId = "";
                }
            }).catch(function (error) { render({ state: "error", error: error.message }); });
        };
        run();
        plugin.state.timer = setInterval(run, 1500);
    }

    plugin.ensureDeviceIntegration = function () {
        if (!plugin.state.nodeId) return false;
        if (!window.pluginHandler || typeof window.pluginHandler.registerPluginTab !== "function") return false;
        window.pluginHandler.registerPluginTab({ tabId: "workspace-device-page", tabTitle: "Pulpit -New" });
        plugin.ensureDeviceTab();
        render();
        return true;
    };

    plugin.ensureDeviceTab = function () {
        if (!document.getElementById("workspace-device-page")) return false;
        var anchor = document.getElementById("MainDevTerminal") || document.getElementById("MainDevPlugins");
        if (!anchor || !anchor.parentNode) return false;
        var tab = document.getElementById("MainDevWorkspace");
        if (!tab) {
            tab = document.createElement("td"); tab.id = "MainDevWorkspace"; tab.tabIndex = 0; tab.className = "topbar_td style3x"; tab.textContent = "Pulpit -New"; tab.onmouseup = plugin.openDeviceTab;
            tab.onkeypress = function (event) { if (event && event.key === "Enter") return plugin.openDeviceTab(event); };
            mark(tab, "Pulpit -New device tab"); anchor.parentNode.insertBefore(tab, anchor.nextSibling);
        }
        tab.style.display = "";
        return true;
    };

    plugin.openDeviceTab = function (event) {
        if (event && ((event.which === 3) || (event.button === 2))) return false;
        if (typeof window.putstore === "function") window.putstore("_curPluginPage", "workspace-device-page");
        if (typeof window.go === "function") window.go(19, event);
        window.setTimeout(function () {
            var header = document.getElementById("p19ph-workspace-device-page");
            if (header && window.pluginHandler && typeof window.pluginHandler.callPluginPage === "function") window.pluginHandler.callPluginPage("workspace-device-page", header);
            render(); plugin.updateDeviceTab(19);
        }, 0);
        if (event && event.preventDefault) event.preventDefault();
        return false;
    };

    plugin.updateDeviceTab = function (view) {
        var tab = document.getElementById("MainDevWorkspace");
        if (!tab) return;
        if (view == null && typeof window.xxcurrentView !== "undefined") view = window.xxcurrentView;
        var activeHeader = document.querySelector("#p19headers span.on");
        var workspaceHeader = document.getElementById("p19ph-workspace-device-page");
        var active = Number(view) === 19 && activeHeader === workspaceHeader;
        tab.classList.remove("style3x", "style3sel"); tab.classList.add(active ? "style3sel" : "style3x");
        var pluginTab = document.getElementById("MainDevPlugins");
        if (pluginTab && active) { pluginTab.classList.remove("style3sel"); pluginTab.classList.add("style3x"); }
        var headers = document.getElementById("p19headers"); if (headers) headers.style.display = active ? "none" : "";
    };

    plugin.onDeviceRefreshEnd = function (nodeId) { plugin.state.nodeId = String(nodeId || ""); if (plugin.state.nodeId) plugin.ensureDeviceIntegration(); };
    plugin.onNativePageEnd = function (view) { if (plugin.state.nodeId) plugin.ensureDeviceTab(); plugin.updateDeviceTab(view); };
    plugin.initialize = function () { if (window.MeshCentralWorkspacePendingNodeId) plugin.state.nodeId = String(window.MeshCentralWorkspacePendingNodeId); if (plugin.state.nodeId) plugin.ensureDeviceIntegration(); return Promise.resolve(); };
    plugin.refresh = plugin.onDeviceRefreshEnd;
    plugin.start = start;
    plugin.stop = stop;
})();