"use strict";
(function () {
    var plugin = window.MeshCentralWorkspace = window.MeshCentralWorkspace || {};
    var busyBySession = Object.create(null);
    var enhanceTimer = null;

    function endpoint(asset, extra) {
        var url = new URL('pluginadmin.ashx', window.location.href);
        url.searchParams.set('pin', 'workspace');
        url.searchParams.set('asset', asset);
        url.searchParams.set('v', '0.9.8-' + Date.now());
        if (extra) Object.keys(extra).forEach(function (key) { url.searchParams.set(key, extra[key]); });
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

    function panel(slot) {
        var busy = !!busyBySession[slot.id] || slot.captureState === 'capturing';
        var ready = slot.captureState === 'ready' && slot.captureVersion;
        var supported = slot.slot === 'user';
        var source = ready ? endpoint('capture-image', { id: slot.id, frame: slot.captureVersion }) : '';
        var status = busy ? 'Pobieranie klatki DXGI...' : (ready ? 'Podglad gotowy' : (slot.captureState === 'error' ? 'Blad przechwytywania' : 'Brak podgladu'));
        return '<div class="workspace-capture">' +
            '<div class="workspace-capture-head"><b>Podglad Workspace</b><button type="button" class="workspace-capture-button"' + (!supported || busy ? ' disabled' : '') + '>Pobierz klatke</button></div>' +
            '<div class="workspace-capture-state ' + (slot.captureState === 'error' ? 'error' : (ready ? 'ok' : '')) + '">' + esc(status) +
            (slot.captureWidth && slot.captureHeight ? ' · ' + esc(slot.captureWidth + ' × ' + slot.captureHeight) : '') +
            (slot.captureBackend ? ' · ' + esc(slot.captureBackend) : '') + '</div>' +
            (supported ? '' : '<div class="workspace-capture-empty">Ukryty desktop bedzie obslugiwany przez backend Virtual Display. DXGI jest teraz wlaczone tylko dla widocznej sesji uzytkownika.</div>') +
            (ready ? '<a class="workspace-capture-link" href="' + source + '" target="_blank" rel="noopener"><img class="workspace-capture-image" src="' + source + '" alt="Podglad Workspace"></a>' :
                (supported ? '<div class="workspace-capture-empty">Natywna pojedyncza klatka przez DXGI Desktop Duplication. Capture dziala jako osobny proces i nie przebudowuje calego interfejsu MeshCentral.</div>' : '')) +
            (slot.captureError ? '<div class="workspace-capture-error">' + esc(slot.captureError) + '</div>' : '') +
            '</div>';
    }

    function refreshUntilDone(sessionId, startedAt) {
        if (!plugin.refresh || !plugin.state || !plugin.state.nodeId) return;
        plugin.refresh(plugin.state.nodeId);
        window.setTimeout(function () {
            var slot = plugin.state && plugin.state.slots && plugin.state.slots.find(function (item) { return item.id === sessionId; });
            var pending = slot && slot.captureState === 'capturing';
            if (pending && Date.now() - startedAt < 25000) {
                refreshUntilDone(sessionId, startedAt);
                return;
            }
            delete busyBySession[sessionId];
            scheduleEnhance();
        }, 900);
    }

    function bind(card, slot) {
        var button = card.querySelector('.workspace-capture-button');
        if (!button) return;
        button.onclick = function (event) {
            if (event) { event.preventDefault(); event.stopPropagation(); }
            if (busyBySession[slot.id]) return false;
            busyBySession[slot.id] = true;
            scheduleEnhance();
            post('capture-frame', { id: slot.id }).then(function () {
                refreshUntilDone(slot.id, Date.now());
            }).catch(function (error) {
                delete busyBySession[slot.id];
                window.alert(error && error.message || error);
                scheduleEnhance();
            });
            return false;
        };
    }

    function enhance() {
        enhanceTimer = null;
        var root = document.getElementById('workspace-device-page');
        if (!root) return;
        Array.prototype.forEach.call(root.querySelectorAll('.workspace-card'), function (card) {
            var slot = getSlot(card.getAttribute('data-slot'));
            var existing = card.querySelector('.workspace-capture');
            if (existing) existing.remove();
            if (!slot || !slot.id || slot.state !== 'running') return;
            var wrapper = document.createElement('div');
            wrapper.innerHTML = panel(slot);
            var node = wrapper.firstChild;
            var apps = card.querySelector('.workspace-apps');
            var debug = card.querySelector('.workspace-debug-toggle');
            if (apps && apps.nextSibling) card.insertBefore(node, apps.nextSibling);
            else if (debug) card.insertBefore(node, debug);
            else card.appendChild(node);
            bind(card, slot);
        });
    }

    function scheduleEnhance() {
        if (enhanceTimer != null) return;
        enhanceTimer = window.setTimeout(enhance, 0);
    }

    var observer = new MutationObserver(function (changes) {
        for (var index = 0; index < changes.length; index++) {
            var target = changes[index].target;
            if (target && (target.id === 'workspace-device-page' || target.closest && target.closest('#workspace-device-page'))) {
                scheduleEnhance();
                break;
            }
        }
    });
    observer.observe(document.body || document.documentElement, { childList: true, subtree: true });
    scheduleEnhance();
})();
