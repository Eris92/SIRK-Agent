'use strict';
(function () {
    var plugin = window.MeshCentralWorkspace = window.MeshCentralWorkspace || {};
    var busyBySession = Object.create(null);
    var enhanceTimer = null;

    function endpoint(asset) {
        var url = new URL('pluginadmin.ashx', window.location.href);
        url.searchParams.set('pin', 'workspace');
        url.searchParams.set('asset', asset);
        url.searchParams.set('v', '0.9.6-' + Date.now());
        return url.href;
    }

    function post(asset, values) {
        var body = new URLSearchParams();
        Object.keys(values || {}).forEach(function (key) { body.set(key, values[key]); });
        return fetch(endpoint(asset), {
            method: 'POST', credentials: 'same-origin', cache: 'no-store',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8' },
            body: body.toString()
        }).then(function (response) {
            return response.json().then(function (data) {
                if (!response.ok || data.ok === false) throw new Error(data.error || response.statusText);
                return data.result;
            });
        });
    }

    function esc(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (character) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character];
        });
    }

    function getSlot(slotId) {
        return plugin.state && Array.isArray(plugin.state.slots) ? plugin.state.slots.find(function (slot) { return slot.slot === slotId; }) : null;
    }

    function signature(slot) {
        return JSON.stringify({ id: slot.id, state: slot.state, appsState: slot.appsState || '', appsError: slot.appsError || '', windows: slot.windows || [], busy: !!busyBySession[slot.id] });
    }

    function panel(slot) {
        var windows = Array.isArray(slot.windows) ? slot.windows : [];
        var busy = !!busyBySession[slot.id] || slot.appsState === 'loading' || slot.appsState === 'launching';
        var state = busy ? '<span class="workspace-app-state">Oczekiwanie na wynik...</span>' : '';
        return '<div class="workspace-apps" data-signature="' + esc(signature(slot)) + '">' +
            '<div class="workspace-app-head"><b>Aplikacje i okna</b><button type="button" class="workspace-app-refresh"' + (busy ? ' disabled' : '') + '>Odśwież okna</button></div>' +
            state +
            '<div class="workspace-app-buttons"><button type="button" data-app="explorer.exe"' + (busy ? ' disabled' : '') + '>Explorer</button><button type="button" data-app="powershell.exe"' + (busy ? ' disabled' : '') + '>PowerShell</button><button type="button" data-app="cmd.exe"' + (busy ? ' disabled' : '') + '>CMD</button><button type="button" data-app="notepad.exe"' + (busy ? ' disabled' : '') + '>Notatnik</button></div>' +
            '<div class="workspace-app-custom"><input class="workspace-app-path" placeholder="C:\\Program Files\\Aplikacja\\app.exe"><input class="workspace-app-args" placeholder="Argumenty"><button type="button" class="workspace-app-launch"' + (busy ? ' disabled' : '') + '>Uruchom</button></div>' +
            '<div class="workspace-window-list">' + (windows.length ? windows.map(function (windowTitle) { return '<div>' + esc(windowTitle) + '</div>'; }).join('') : '<span>Brak pobranej listy okien.</span>') + '</div>' +
            (slot.appsError ? '<div class="workspace-app-error">' + esc(slot.appsError) + '</div>' : '') +
            '</div>';
    }

    function refreshUntilDone(sessionId, startedAt) {
        if (!plugin.refresh || !plugin.state || !plugin.state.nodeId) return;
        plugin.refresh(plugin.state.nodeId);
        window.setTimeout(function () {
            var slot = plugin.state && plugin.state.slots && plugin.state.slots.find(function (item) { return item.id === sessionId; });
            var pending = slot && (slot.appsState === 'loading' || slot.appsState === 'launching');
            if (pending && Date.now() - startedAt < 15000) {
                refreshUntilDone(sessionId, startedAt);
                return;
            }
            delete busyBySession[sessionId];
            scheduleEnhance();
        }, 900);
    }

    function execute(slot, asset, values) {
        if (!slot || !slot.id || busyBySession[slot.id]) return;
        busyBySession[slot.id] = true;
        scheduleEnhance();
        post(asset, values).then(function () {
            refreshUntilDone(slot.id, Date.now());
        }).catch(function (error) {
            delete busyBySession[slot.id];
            window.alert(error && error.message || error);
            scheduleEnhance();
        });
    }

    function bind(card, slot) {
        var refresh = card.querySelector('.workspace-app-refresh');
        if (refresh) refresh.onclick = function (event) {
            if (event) { event.preventDefault(); event.stopPropagation(); }
            execute(slot, 'apps-list', { id: slot.id });
            return false;
        };
        Array.prototype.forEach.call(card.querySelectorAll('[data-app]'), function (button) {
            button.onclick = function (event) {
                if (event) { event.preventDefault(); event.stopPropagation(); }
                execute(slot, 'apps-launch', { id: slot.id, file: button.getAttribute('data-app'), args: '' });
                return false;
            };
        });
        var launch = card.querySelector('.workspace-app-launch');
        if (launch) launch.onclick = function (event) {
            if (event) { event.preventDefault(); event.stopPropagation(); }
            var path = card.querySelector('.workspace-app-path');
            var args = card.querySelector('.workspace-app-args');
            execute(slot, 'apps-launch', { id: slot.id, file: path && path.value || '', args: args && args.value || '' });
            return false;
        };
    }

    function enhance() {
        enhanceTimer = null;
        var root = document.getElementById('workspace-device-page');
        if (!root) return;
        Array.prototype.forEach.call(root.querySelectorAll('.workspace-card'), function (card) {
            var slot = getSlot(card.getAttribute('data-slot'));
            var existing = card.querySelector('.workspace-apps');
            if (!slot || !slot.id || slot.state !== 'running') {
                if (existing) existing.remove();
                return;
            }
            var expected = signature(slot);
            if (existing && existing.getAttribute('data-signature') === expected) return;
            var wrapper = document.createElement('div');
            wrapper.innerHTML = panel(slot);
            var node = wrapper.firstChild;
            if (existing) existing.replaceWith(node);
            else {
                var debug = card.querySelector('.workspace-debug-toggle');
                if (debug) card.insertBefore(node, debug); else card.appendChild(node);
            }
            bind(card, slot);
        });
    }

    function scheduleEnhance() {
        if (enhanceTimer != null) return;
        enhanceTimer = window.setTimeout(enhance, 0);
    }

    var rootObserver = new MutationObserver(function (changes) {
        for (var i = 0; i < changes.length; i++) {
            var target = changes[i].target;
            if (target && (target.id === 'workspace-device-page' || target.closest && target.closest('#workspace-device-page'))) {
                scheduleEnhance();
                break;
            }
        }
    });
    rootObserver.observe(document.body || document.documentElement, { childList: true, subtree: true });
    scheduleEnhance();
})();