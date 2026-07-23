"use strict";

(function () {
    window.MeshCentralWorkspace = window.MeshCentralWorkspace || {};
    var plugin = window.MeshCentralWorkspace;
    plugin.state = plugin.state || { nodeId: "", slots: [], timer: null };

    function assetUrl(asset, extra) {
        var url = new URL("pluginadmin.ashx", window.location.href);
        url.searchParams.set("pin", "workspace");
        url.searchParams.set("asset", asset);
        url.searchParams.set("v", "0.7.0");
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

    function resolution(width, height) { return width == null || height == null ? "-" : width + " × " + height; }
    function busy(state) { return ["requested", "deploying", "stopping"].indexOf(state) >= 0; }
    function active(slot) { return slot && ["free", "stopped", "error"].indexOf(slot.state) < 0; }
    function title(slot) {
        if (slot.slot === "user") return "Sesja użytkownika";
        if (slot.slot === "admin1") return "Workspace A";
        if (slot.slot === "admin2") return "Workspace B";
        return slot.slotLabel || slot.slot;
    }
    function subtitle(slot) { return slot.slot === "user" ? "Widoczny pulpit użytkownika" : "Ukryty pulpit administracyjny"; }
    function expectedDesktop(slot) {
        if (slot.slot === "admin1") return "SirK-Admin-1";
        if (slot.slot === "admin2") return "SirK-Admin-2";
        return "default";
    }

    function card(slot) {
        var occupied = active(slot);
        var disabledStart = busy(slot.state) || occupied;
        var disabledStop = !occupied || busy(slot.state);
        var startLabel = slot.slot === "user" ? "Przygotuj" : "Utwórz";
        return '<section class="workspace-card" data-slot="' + escapeHtml(slot.slot) + '">' +
            '<div class="workspace-card-head"><div><h3>' + escapeHtml(title(slot)) + '</h3><span class="workspace-kind">' + escapeHtml(subtitle(slot)) + '</span></div>' +
            '<div class="workspace-toolbar"><button class="btn btn-success btn-sm workspace-start"' + (disabledStart ? ' disabled' : '') + '>' + startLabel + '</button>' +
            '<button class="btn btn-danger btn-sm workspace-stop"' + (disabledStop ? ' disabled' : '') + '>Zatrzymaj</button></div></div>' +
            '<dl class="workspace-grid">' +
            '<dt>Stan</dt><dd>' + escapeHtml(slot.state || "free") + '</dd>' +
            '<dt>Właściciel</dt><dd>' + escapeHtml(slot.ownerName || "-") + '</dd>' +
            '<dt>Session ID</dt><dd>' + escapeHtml(slot.id || "-") + '</dd>' +
            '<dt>Bootstrap PID</dt><dd>' + escapeHtml(slot.bootstrapPid || "-") + '</dd>' +
            '<dt>Worker PID</dt><dd>' + escapeHtml(slot.pid || "-") + '</dd>' +
            '<dt>Windows Session</dt><dd>' + escapeHtml(slot.windowsSessionId == null ? "-" : slot.windowsSessionId) + '</dd>' +
            '<dt>User</dt><dd>' + escapeHtml(slot.user || "-") + '</dd>' +
            '<dt>Desktop</dt><dd>' + escapeHtml(slot.desktop || expectedDesktop(slot)) + '</dd>' +
            '<dt>Izolacja</dt><dd>' + escapeHtml(slot.slot === "user" ? "Nie" : "Tak - niewidoczny dla użytkownika") + '</dd>' +
            '<dt>Version</dt><dd>' + escapeHtml(slot.version || "-") + '</dd>' +
            '<dt>Monitory</dt><dd>' + escapeHtml(slot.monitorCount == null ? "-" : slot.monitorCount) + '</dd>' +
            '<dt>Ekran główny</dt><dd>' + escapeHtml(resolution(slot.primaryWidth, slot.primaryHeight)) + '</dd>' +
            '<dt>Pulpit wirtualny</dt><dd>' + escapeHtml(resolution(slot.virtualWidth, slot.virtualHeight)) + '</dd>' +
            '<dt>Błąd</dt><dd>' + escapeHtml(slot.error || "-") + '</dd></dl></section>';
    }

    function render() {
        var root = document.getElementById("workspace-device-page");
        if (!root) return;
        root.className = "workspace-panel";
        root.innerHTML = '<div class="workspace-header"><div><h2>Workspace</h2><p>Host: ' + escapeHtml(plugin.state.nodeId || "-") + '</p></div><button id="workspace-refresh" class="btn btn-primary btn-sm">Odśwież</button></div>' +
            '<div class="workspace-cards">' + plugin.state.slots.map(card).join("") + '</div>' +
            '<p class="workspace-note">Workspace A i Workspace B działają na ukrytych desktopach winsta0\\SirK-Admin-1 oraz winsta0\\SirK-Admin-2. Użytkownik nie widzi ich okien.</p>';
        var refresh = document.getElementById("workspace-refresh");
        if (refresh) refresh.onclick = loadSlots;
        Array.prototype.forEach.call(root.querySelectorAll(".workspace-card"), function (element) {
            var slotId = element.getAttribute("data-slot");
            var startButton = element.querySelector(".workspace-start");
            var stopButton = element.querySelector(".workspace-stop");
            if (startButton) startButton.onclick = function () { start(slotId); };
            if (stopButton) stopButton.onclick = function () {
                var slot = plugin.state.slots.find(function (item) { return item.slot === slotId; });
                if (slot && slot.id) stop(slot.id);
            };
        });
    }

    function loadSlots() {
        if (!plugin.state.nodeId) return Promise.resolve([]);
        return request("slots", null, { nodeId: plugin.state.nodeId }).then(function (slots) {
            plugin.state.slots = slots || [];
            render();
            return plugin.state.slots;
        }).catch(function (error) {
            plugin.state.slots = [{ slot: "user", slotLabel: "Błąd", kind: "user", state: "error", error: error.message }];
            render();
            return [];
        });
    }

    function start(slot) {
        post("start", { nodeId: plugin.state.nodeId, slot: slot }).then(function () { loadSlots(); startPolling(); })
            .catch(function (error) { alert(error.message); loadSlots(); });
    }

    function stop(id) {
        post("stop", { id: id }).then(function () { loadSlots(); startPolling(); })
            .catch(function (error) { alert(error.message); loadSlots(); });
    }

    function startPolling() {
        if (plugin.state.timer) clearInterval(plugin.state.timer);
        plugin.state.timer = setInterval(loadSlots, 1500);
    }

    plugin.ensureDeviceIntegration = function () {
        if (!plugin.state.nodeId) return false;
        if (!window.pluginHandler || typeof window.pluginHandler.registerPluginTab !== "function") return false;
        window.pluginHandler.registerPluginTab({ tabId: "workspace-device-page", tabTitle: "Pulpit -New" });
        plugin.ensureDeviceTab();
        loadSlots();
        return true;
    };

    plugin.ensureDeviceTab = function () {
        if (!document.getElementById("workspace-device-page")) return false;
        var anchor = document.getElementById("MainDevTerminal") || document.getElementById("MainDevPlugins");
        if (!anchor || !anchor.parentNode) return false;
        var tab = document.getElementById("MainDevWorkspace");
        if (!tab) {
            tab = document.createElement("td"); tab.id = "MainDevWorkspace"; tab.tabIndex = 0; tab.className = "topbar_td style3x"; tab.textContent = "Pulpit -New"; tab.onmouseup = plugin.openDeviceTab;
            anchor.parentNode.insertBefore(tab, anchor.nextSibling);
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
            loadSlots();
        }, 0);
        if (event && event.preventDefault) event.preventDefault();
        return false;
    };

    plugin.onDeviceRefreshEnd = function (nodeId) { plugin.state.nodeId = String(nodeId || ""); if (plugin.state.nodeId) plugin.ensureDeviceIntegration(); };
    plugin.onNativePageEnd = function () { if (plugin.state.nodeId) plugin.ensureDeviceTab(); };
    plugin.initialize = function () { if (window.MeshCentralWorkspacePendingNodeId) plugin.state.nodeId = String(window.MeshCentralWorkspacePendingNodeId); if (plugin.state.nodeId) plugin.ensureDeviceIntegration(); startPolling(); return Promise.resolve(); };
    plugin.refresh = plugin.onDeviceRefreshEnd;
})();
