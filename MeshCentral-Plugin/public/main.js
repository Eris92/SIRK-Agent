"use strict";

(function () {
    window.MeshCentralWorkspace = window.MeshCentralWorkspace || {};
    var plugin = window.MeshCentralWorkspace;
    plugin.state = plugin.state || { nodeId: "", slots: [], timer: null, actions: {}, debugOpen: {}, devicesOpen: {} };
    plugin.state.actions = plugin.state.actions || {};
    plugin.state.debugOpen = plugin.state.debugOpen || {};
    plugin.state.devicesOpen = plugin.state.devicesOpen || {};

    function assetUrl(asset, extra) {
        var url = new URL("pluginadmin.ashx", window.location.href);
        url.searchParams.set("pin", "workspace");
        url.searchParams.set("asset", asset);
        url.searchParams.set("v", "0.8.6");
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

    function escapeHtml(value) { return String(value == null ? "" : value).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]; }); }
    function resolution(width, height) { return width == null || height == null ? "-" : width + " × " + height; }
    function busy(state) { return ["requested", "deploying", "stopping", "wysyłanie", "zatrzymywanie"].indexOf(state) >= 0; }
    function active(slot) { return slot && ["free", "stopped", "error"].indexOf(slot.state) < 0; }
    function title(slot) { if (slot.slot === "user") return "Sesja użytkownika"; if (slot.slot === "admin1") return "Workspace A"; if (slot.slot === "admin2") return "Workspace B"; return slot.slotLabel || slot.slot; }
    function subtitle(slot) { return slot.slot === "user" ? "Widoczny pulpit użytkownika" : "Ukryty pulpit administracyjny"; }
    function expectedDesktop(slot) { if (slot.slot === "admin1") return "SirK-Admin-1"; if (slot.slot === "admin2") return "SirK-Admin-2"; return "default"; }
    function stateLabel(state) { return ({ free: "Wolny", requested: "Żądanie", deploying: "Uruchamianie", running: "Działa", stopping: "Zatrzymywanie", stopped: "Zatrzymany", error: "Błąd", "wysyłanie": "Wysyłanie", "zatrzymywanie": "Zatrzymywanie" })[state] || state || "Wolny"; }
    function stateClass(state) { if (state === "running") return "ok"; if (state === "error") return "error"; if (busy(state)) return "pending"; return "idle"; }
    function check(label, ok, pending) { var cls = pending ? "pending" : (ok ? "ok" : "off"); var icon = pending ? "…" : (ok ? "✓" : "–"); return '<span class="workspace-check ' + cls + '"><b>' + icon + '</b>' + escapeHtml(label) + '</span>'; }

    function deviceKey(slot) { return "workspace-device-mode:" + plugin.state.nodeId + ":" + slot; }
    function getDeviceMode(slot) { try { return localStorage.getItem(deviceKey(slot)) || "broker"; } catch (error) { return "broker"; } }
    function setDeviceMode(slot, mode) { try { localStorage.setItem(deviceKey(slot), mode); } catch (error) {} render(); }

    function displaySlot(slot) {
        var action = plugin.state.actions[slot.slot];
        if (!action) return slot;
        var copy = Object.assign({}, slot);
        copy.state = action.state;
        copy.error = action.error || slot.error || null;
        return copy;
    }

    function health(slot) {
        var running = slot.state === "running";
        var pending = busy(slot.state);
        return '<div class="workspace-health">' +
            check("Proces", !!slot.pid, pending) +
            check("Heartbeat", running && !slot.error, pending) +
            check("Pipe", running && !!slot.pid, pending) +
            check("Desktop", running && !!slot.desktop, pending) +
            check("GUI", slot.slot === "user" ? running : !!slot.testWindowReady, pending) +
            check("Capture", false, false) +
            check("Input", false, false) +
            check("Clipboard", false, false) +
            '</div>';
    }

    function devicePanel(slot) {
        var open = !!plugin.state.devicesOpen[slot.slot];
        var mode = getDeviceMode(slot.slot);
        function modeButton(id, label, description) {
            return '<button type="button" class="workspace-device-mode' + (mode === id ? ' selected' : '') + '" data-mode="' + id + '"><strong>' + escapeHtml(label) + '</strong><span>' + escapeHtml(description) + '</span></button>';
        }
        return '<button type="button" class="workspace-devices-toggle">' + (open ? 'Ukryj urządzenia' : 'Urządzenia') + '</button>' +
            '<div class="workspace-devices"' + (open ? '' : ' hidden') + '>' +
            '<h4>Tryb urządzeń</h4><p>Wybór jest zapisywany dla tego hosta i Workspace. Transport urządzeń będzie uruchamiany etapami.</p>' +
            '<div class="workspace-device-modes">' +
            modeButton('broker', 'Device Broker', 'PIV / Smart Card, audio, kamera i urządzenia obsługiwane logicznie') +
            modeButton('passthrough', 'USB Passthrough', 'Pełne urządzenie USB, np. dongle, programator, pendrive lub adapter') +
            modeButton('virtual-media', 'Virtual Media', 'Obrazy ISO / IMG jako zdalny napęd CD/DVD lub dysk') +
            '</div>' +
            '<div class="workspace-device-status"><b>Wybrano:</b> ' + escapeHtml(mode === 'broker' ? 'Device Broker' : (mode === 'passthrough' ? 'USB Passthrough' : 'Virtual Media')) +
            '<span>Moduł transportu: w przygotowaniu</span></div></div>';
    }

    function card(sourceSlot) {
        var slot = displaySlot(sourceSlot);
        var occupied = active(slot);
        var actionPending = !!plugin.state.actions[slot.slot];
        var disabledStart = actionPending || busy(slot.state) || occupied;
        var disabledStop = actionPending || !occupied || busy(slot.state);
        var startLabel = slot.slot === "user" ? "Przygotuj" : "Utwórz";
        var debugOpen = !!plugin.state.debugOpen[slot.slot];
        return '<section class="workspace-card" data-slot="' + escapeHtml(slot.slot) + '">' +
            '<div class="workspace-card-head"><div><h3>' + escapeHtml(title(slot)) + '</h3><span class="workspace-kind">' + escapeHtml(subtitle(slot)) + '</span></div>' +
            '<div class="workspace-toolbar"><button type="button" class="btn btn-success btn-sm workspace-start"' + (disabledStart ? ' disabled' : '') + '>' + startLabel + '</button>' +
            '<button type="button" class="btn btn-danger btn-sm workspace-stop"' + (disabledStop ? ' disabled' : '') + '>Zatrzymaj</button></div></div>' +
            '<div class="workspace-state ' + stateClass(slot.state) + '"><span></span><strong>' + escapeHtml(stateLabel(slot.state)) + '</strong>' + (slot.error ? '<em>' + escapeHtml(slot.error) + '</em>' : '') + '</div>' +
            health(slot) +
            '<dl class="workspace-grid workspace-summary">' +
            '<dt>Właściciel</dt><dd>' + escapeHtml(slot.ownerName || "-") + '</dd>' +
            '<dt>Użytkownik</dt><dd>' + escapeHtml(slot.user || "-") + '</dd>' +
            '<dt>Sesja Windows</dt><dd>' + escapeHtml(slot.windowsSessionId == null ? "-" : slot.windowsSessionId) + '</dd>' +
            '<dt>Desktop</dt><dd>' + escapeHtml(slot.desktop || expectedDesktop(slot)) + '</dd>' +
            '<dt>WorkspaceHost</dt><dd>' + escapeHtml(slot.version || "-") + '</dd>' +
            '<dt>Proces</dt><dd>' + escapeHtml(slot.pid ? ("PID " + slot.pid) : "-") + '</dd>' +
            '<dt>Ekran</dt><dd>' + escapeHtml(resolution(slot.primaryWidth, slot.primaryHeight)) + '</dd>' +
            '<dt>Monitory</dt><dd>' + escapeHtml(slot.monitorCount == null ? "-" : slot.monitorCount) + '</dd></dl>' +
            devicePanel(slot) +
            '<button type="button" class="workspace-debug-toggle">' + (debugOpen ? "Ukryj debug" : "Pokaż debug") + '</button>' +
            '<div class="workspace-debug"' + (debugOpen ? '' : ' hidden') + '><dl class="workspace-grid">' +
            '<dt>Session ID</dt><dd>' + escapeHtml(slot.id || "-") + '</dd>' +
            '<dt>Bootstrap PID</dt><dd>' + escapeHtml(slot.bootstrapPid || "-") + '</dd>' +
            '<dt>Worker PID</dt><dd>' + escapeHtml(slot.pid || "-") + '</dd>' +
            '<dt>Izolacja</dt><dd>' + escapeHtml(slot.slot === "user" ? "Nie" : "Tak - niewidoczny dla użytkownika") + '</dd>' +
            '<dt>Okno testowe</dt><dd>' + escapeHtml(slot.slot === "user" ? "Nie dotyczy" : (slot.testWindowReady ? "Tak" : "Nie")) + '</dd>' +
            '<dt>Tytuł okna</dt><dd>' + escapeHtml(slot.testWindowTitle || "-") + '</dd>' +
            '<dt>Wątek UI</dt><dd>' + escapeHtml(slot.testWindowThreadId || "-") + '</dd>' +
            '<dt>Pulpit wirtualny</dt><dd>' + escapeHtml(resolution(slot.virtualWidth, slot.virtualHeight)) + '</dd>' +
            '<dt>Ostatni wynik</dt><dd><pre>' + escapeHtml(slot.lastOutput || "-") + '</pre></dd>' +
            '<dt>Błąd</dt><dd>' + escapeHtml(slot.error || "-") + '</dd></dl></div></section>';
    }

    function bindButtons(root) {
        var refresh = document.getElementById("workspace-refresh");
        if (refresh) refresh.onclick = function (event) { if (event) event.preventDefault(); loadSlots(); return false; };
        Array.prototype.forEach.call(root.querySelectorAll(".workspace-card"), function (element) {
            var slotId = element.getAttribute("data-slot");
            var startButton = element.querySelector(".workspace-start");
            var stopButton = element.querySelector(".workspace-stop");
            var debugButton = element.querySelector(".workspace-debug-toggle");
            var devicesButton = element.querySelector(".workspace-devices-toggle");
            if (startButton) startButton.onclick = function (event) { if (event) { event.preventDefault(); event.stopPropagation(); } start(slotId); return false; };
            if (stopButton) stopButton.onclick = function (event) { if (event) { event.preventDefault(); event.stopPropagation(); } var slot = plugin.state.slots.find(function (item) { return item.slot === slotId; }); stop(slot); return false; };
            if (debugButton) debugButton.onclick = function (event) { if (event) event.preventDefault(); plugin.state.debugOpen[slotId] = !plugin.state.debugOpen[slotId]; render(); return false; };
            if (devicesButton) devicesButton.onclick = function (event) { if (event) event.preventDefault(); plugin.state.devicesOpen[slotId] = !plugin.state.devicesOpen[slotId]; render(); return false; };
            Array.prototype.forEach.call(element.querySelectorAll(".workspace-device-mode"), function (button) {
                button.onclick = function (event) { if (event) event.preventDefault(); setDeviceMode(slotId, button.getAttribute("data-mode")); return false; };
            });
        });
    }

    function render() {
        var root = document.getElementById("workspace-device-page");
        if (!root) return;
        root.className = "workspace-panel";
        root.innerHTML = '<div class="workspace-header"><div><h2>Workspace</h2><p>Host: ' + escapeHtml(plugin.state.nodeId || "-") + '</p></div><button type="button" id="workspace-refresh" class="btn btn-primary btn-sm">Odśwież</button></div>' +
            '<div class="workspace-cards">' + plugin.state.slots.map(card).join("") + '</div>';
        bindButtons(root);
    }

    function loadSlots() {
        if (!plugin.state.nodeId) return Promise.resolve([]);
        return request("slots", null, { nodeId: plugin.state.nodeId }).then(function (slots) { plugin.state.slots = slots || []; render(); return plugin.state.slots; })
            .catch(function (error) { plugin.state.slots = [{ slot: "user", slotLabel: "Błąd", kind: "user", state: "error", error: error.message }]; render(); return []; });
    }

    function start(slot) {
        if (!slot || plugin.state.actions[slot]) return;
        plugin.state.actions[slot] = { state: "wysyłanie" }; render();
        post("start", { nodeId: plugin.state.nodeId, slot: slot }).then(function () { delete plugin.state.actions[slot]; return loadSlots(); }).then(startPolling)
            .catch(function (error) { plugin.state.actions[slot] = { state: "error", error: error.message }; render(); });
    }

    function stop(slot) {
        if (!slot || !slot.id || plugin.state.actions[slot.slot]) return;
        plugin.state.actions[slot.slot] = { state: "zatrzymywanie" }; render();
        post("stop", { id: slot.id }).then(function () { delete plugin.state.actions[slot.slot]; return loadSlots(); }).then(startPolling)
            .catch(function (error) { plugin.state.actions[slot.slot] = { state: "error", error: error.message }; render(); });
    }

    function startPolling() { if (plugin.state.timer) clearInterval(plugin.state.timer); plugin.state.timer = setInterval(loadSlots, 1500); }

    plugin.ensureDeviceIntegration = function () { if (!plugin.state.nodeId) return false; if (!window.pluginHandler || typeof window.pluginHandler.registerPluginTab !== "function") return false; window.pluginHandler.registerPluginTab({ tabId: "workspace-device-page", tabTitle: "Pulpit -New" }); plugin.ensureDeviceTab(); loadSlots(); return true; };
    plugin.ensureDeviceTab = function () { if (!document.getElementById("workspace-device-page")) return false; var anchor = document.getElementById("MainDevTerminal") || document.getElementById("MainDevPlugins"); if (!anchor || !anchor.parentNode) return false; var tab = document.getElementById("MainDevWorkspace"); if (!tab) { tab = document.createElement("td"); tab.id = "MainDevWorkspace"; tab.tabIndex = 0; tab.className = "topbar_td style3x"; tab.textContent = "Pulpit -New"; tab.onmouseup = plugin.openDeviceTab; anchor.parentNode.insertBefore(tab, anchor.nextSibling); } tab.style.display = ""; return true; };
    plugin.openDeviceTab = function (event) { if (event && ((event.which === 3) || (event.button === 2))) return false; if (typeof window.putstore === "function") window.putstore("_curPluginPage", "workspace-device-page"); if (typeof window.go === "function") window.go(19, event); window.setTimeout(function () { var header = document.getElementById("p19ph-workspace-device-page"); if (header && window.pluginHandler && typeof window.pluginHandler.callPluginPage === "function") window.pluginHandler.callPluginPage("workspace-device-page", header); plugin.ensureDeviceIntegration(); }, 0); if (event && event.preventDefault) event.preventDefault(); return false; };
    plugin.onDeviceRefreshEnd = function (nodeId) { plugin.state.nodeId = String(nodeId || ""); if (plugin.state.nodeId) plugin.ensureDeviceIntegration(); };
    plugin.onNativePageEnd = function () { if (plugin.state.nodeId) plugin.ensureDeviceTab(); };
    plugin.initialize = function () { if (window.MeshCentralWorkspacePendingNodeId) plugin.state.nodeId = String(window.MeshCentralWorkspacePendingNodeId); if (plugin.state.nodeId) plugin.ensureDeviceIntegration(); startPolling(); return Promise.resolve(); };
    plugin.refresh = plugin.onDeviceRefreshEnd;
})();