"use strict";
(function () {
    var plugin = window.MeshCentralWorkspace = window.MeshCentralWorkspace || {};
    var busyBySession = Object.create(null);
    var enhanceTimer = null;

    function endpoint(asset, extra) {
        var url = new URL('pluginadmin.ashx', window.location.href);
        url.searchParams.set('pin', 'workspace');
        url.searchParams.set('asset', asset);
        url.searchParams.set('v', '0.9.9-' + Date.now());
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
            return response.text().then(function (text) {
                var data;
                try { data = JSON.parse(text || '{}'); } catch (error) { throw new Error(text || response.statusText || 'Invalid server response'); }
                if (!response.ok || data.ok === false) throw new Error(data.error || response.statusText || 'Capture request failed');
                return data.result;
            });
        });
    }

    function esc(value) {
        return String(value == null ? '' : value).replace(/[&<>"']/g, function (character) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character];
        });
    }

    function getSlotByName(slotName) {
        return plugin.state && Array.isArray(plugin.state.slots) ? plugin.state.slots.find(function (slot) { return slot.slot === slotName; }) : null;
    }

    function getSlotBySession(sessionId) {
        return plugin.state && Array.isArray(plugin.state.slots) ? plugin.state.slots.find(function (slot) { return slot.id === sessionId; }) : null;
    }

    function signature(slot) {
        return [slot.id, slot.state, slot.captureState, slot.captureVersion, slot.captureWidth, slot.captureHeight, slot.captureBackend, slot.captureError, !!busyBySession[slot.id]].join('|');
    }

    function panel(slot) {
        var busy = !!busyBySession[slot.id] || slot.captureState === 'capturing';
        var ready = slot.captureState === 'ready' && slot.captureVersion;
        var supported = slot.slot === 'user';
        var source = ready ? endpoint('capture-image', { id: slot.id, frame: slot.captureVersion }) : '';
        var status = busy ? 'Pobieranie klatki DXGI...' : (ready ? 'Podglad gotowy' : (slot.captureState === 'error' ? 'Blad przechwytywania' : 'Brak podgladu'));
        return '<div class="workspace-capture" data-capture-signature="' + esc(signature(slot)) + '">' +
            '<div class="workspace-capture-head"><b>Podglad Workspace</b><button type="button" class="workspace-capture-button" data-session-id="' + esc(slot.id) + '" data-slot="' + esc(slot.slot) + '"' + (!supported || busy ? ' disabled' : '') + '>Pobierz klatke</button></div>' +
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
        if (!plugin.refresh || !plugin.state || !plugin.state.nodeId) {
            delete busyBySession[sessionId];
            scheduleEnhance();
            return;
        }
        Promise.resolve(plugin.refresh(plugin.state.nodeId)).finally(function () {
            window.setTimeout(function () {
                var slot = getSlotBySession(sessionId);
                var pending = slot && slot.captureState === 'capturing';
                if (pending && Date.now() - startedAt < 45000) {
                    refreshUntilDone(sessionId, startedAt);
                    return;
                }
                delete busyBySession[sessionId];
                scheduleEnhance();
            }, 900);
        });
    }

    function executeCapture(sessionId) {
        if (!sessionId || busyBySession[sessionId]) return;
        busyBySession[sessionId] = true;
        scheduleEnhance();
        post('capture-frame', { id: sessionId }).then(function () {
            refreshUntilDone(sessionId, Date.now());
        }).catch(function (error) {
            delete busyBySession[sessionId];
            window.alert(error && error.message || error);
            scheduleEnhance();
        });
    }

    function enhance() {
        enhanceTimer = null;
        var root = document.getElementById('workspace-device-page');
        if (!root) return;
        Array.prototype.forEach.call(root.querySelectorAll('.workspace-card'), function (card) {
            var slot = getSlotByName(card.getAttribute('data-slot'));
            var existing = card.querySelector('.workspace-capture');
            if (!slot || !slot.id || slot.state !== 'running') {
                if (existing) existing.remove();
                return;
            }
            var expectedSignature = signature(slot);
            if (existing && existing.getAttribute('data-capture-signature') === expectedSignature) return;
            var wrapper = document.createElement('div');
            wrapper.innerHTML = panel(slot);
            var node = wrapper.firstChild;
            if (existing) { existing.replaceWith(node); return; }
            var apps = card.querySelector('.workspace-apps');
            var debug = card.querySelector('.workspace-debug-toggle');
            if (apps && apps.nextSibling) card.insertBefore(node, apps.nextSibling);
            else if (debug) card.insertBefore(node, debug);
            else card.appendChild(node);
        });
    }

    function scheduleEnhance() {
        if (enhanceTimer != null) return;
        enhanceTimer = window.setTimeout(enhance, 0);
    }

    document.addEventListener('click', function (event) {
        var button = event.target && event.target.closest ? event.target.closest('.workspace-capture-button') : null;
        if (!button || button.disabled) return;
        event.preventDefault();
        event.stopPropagation();
        executeCapture(button.getAttribute('data-session-id'));
    }, true);

    var observer = new MutationObserver(function (changes) {
        for (var index = 0; index < changes.length; index++) {
            var target = changes[index].target;
            if (!target) continue;
            if (target.classList && target.classList.contains('workspace-capture')) continue;
            if (target.closest && target.closest('.workspace-capture')) continue;
            if (target.id === 'workspace-device-page' || target.closest && target.closest('#workspace-device-page')) {
                scheduleEnhance();
                break;
            }
        }
    });
    observer.observe(document.body || document.documentElement, { childList: true, subtree: true });
    scheduleEnhance();
})();