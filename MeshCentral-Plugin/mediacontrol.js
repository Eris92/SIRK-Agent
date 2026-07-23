'use strict';

const crypto = require('crypto');

module.exports.createMediaControl = function createMediaControl(parent, workspaceModule) {
    const outputs = Object.create(null);
    const pendingByNode = Object.create(null);

    function makeId() { return crypto.randomBytes(12).toString('hex'); }
    function userId(user) { return String(user && user._id || ''); }
    function getWebServer() { return (parent && parent.parent && parent.parent.webserver) || (parent && parent.webServer) || (parent && parent.parent) || null; }
    function getDomain(user) {
        const webServer = getWebServer();
        const domainId = userId(user).split('/')[1] || '';
        return webServer && webServer.domains && webServer.domains[domainId] ||
            parent.parent && parent.parent.config && parent.parent.config.domains && parent.parent.config.domains[domainId] || { id: domainId };
    }
    function normalizeNodeId(nodeId, domain) { let value = String(nodeId || ''); if (value.indexOf('/') < 0) value = 'node/' + domain.id + '/' + value; return value; }
    function getSession(user, sessionId) {
        const session = workspaceModule.sessions.get(String(sessionId || ''));
        if (!session) throw new Error('Workspace session not found.');
        if (session.ownerId && session.ownerId !== userId(user) && user.siteadmin !== 0xFFFFFFFF) throw new Error('Workspace belongs to ' + session.ownerName + '.');
        if (session.state !== 'running') throw new Error('Workspace must be running.');
        return session;
    }

    function resultLine(sessionId, state, dataExpression) {
        return "$r=[ordered]@{sessionId='" + String(sessionId).replace(/'/g, "''") + "';state='" + state + "';data=" + dataExpression + "};$j=$r|ConvertTo-Json -Compress -Depth 10;$b=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($j));Write-Output ('__WORKSPACE_MEDIA_DEVICES_B64__'+$b+'__END__')";
    }

    function enumerateCommand(session) {
        const success = resultLine(session.id, 'listed', '([ordered]@{cameras=@($cameras);microphones=@($microphones);speakers=@($speakers);captureEnabled=$false;recordingEnabled=$false;liveStreamingEnabled=$false})');
        const failure = resultLine(session.id, 'error', '([ordered]@{message=$_.Exception.Message})');
        return [
            "$ErrorActionPreference='Stop'", 'try{',
            "$all=Get-CimInstance Win32_PnPEntity | Where-Object {$_.Status -eq 'OK'}",
            "$cameras=@($all|Where-Object {$_.PNPClass -in @('Camera','Image') -or $_.Name -match 'camera|webcam'}|Select-Object -ExpandProperty Name -Unique)",
            "$audio=@($all|Where-Object {$_.PNPClass -in @('AudioEndpoint','Media') -or $_.Name -match 'microphone|mikrofon|speaker|glosnik'}|Select-Object -ExpandProperty Name -Unique)",
            "$microphones=@($audio|Where-Object {$_ -match 'microphone|mikrofon|mic'})",
            "$speakers=@($audio|Where-Object {$_ -notmatch 'microphone|mikrofon|mic'})",
            success,
            '}catch{' + failure + '}'
        ].join(';');
    }

    function dispatch(session, user, commandText, responseId) {
        return new Promise(function (resolve, reject) {
            const webServer = getWebServer(); const domain = getDomain(user);
            if (!webServer || !domain || typeof webServer.GetNodeWithRights !== 'function') return reject(new Error('MeshCentral device API is unavailable.'));
            const nodeId = normalizeNodeId(session.nodeId, domain);
            webServer.GetNodeWithRights(domain, user, nodeId, function (node, rights, visible) {
                if (!node || rights === 0 || visible === false) return reject(new Error('You do not have access to this device.'));
                if (((rights & 24) !== 24) && ((rights & 0x00020000) === 0)) return reject(new Error('You do not have permission to run commands on this device.'));
                const command = { action: 'runcommands', type: 2, cmds: commandText, runAsUser: 2, sessionid: session.id, reply: true, responseid: responseId };
                const agents = webServer.wsagents || webServer.parent && webServer.parent.wsagents || parent.parent && parent.parent.wsagents || {};
                const agent = agents[nodeId]; outputs[responseId] = { output: '', sessionId: session.id }; pendingByNode[nodeId] = responseId;
                if (agent && agent.authenticated === 2 && agent.agentInfo) { try { agent.send(JSON.stringify(command)); return resolve(session); } catch (error) { return reject(error); } }
                const multiServer = webServer.multiServer || webServer.parent && webServer.parent.multiServer || parent.parent && parent.parent.multiServer;
                if (multiServer) { try { multiServer.DispatchMessage({ action: 'agentCommand', nodeid: nodeId, command }); return resolve(session); } catch (error) { return reject(error); } }
                reject(new Error('Device agent is not connected.'));
            });
        });
    }

    function list(user, sessionId) {
        const session = getSession(user, sessionId);
        session.mediaBrokerState = 'loading'; session.mediaBrokerError = null;
        return dispatch(session, user, enumerateCommand(session), 'workspace-media-devices-' + makeId());
    }

    function consume(responseId, raw) {
        const item = outputs[responseId]; if (!item) return false;
        item.output = (item.output + String(raw == null ? '' : raw)).slice(-2 * 1024 * 1024);
        const match = item.output.match(/__WORKSPACE_MEDIA_DEVICES_B64__([A-Za-z0-9+/=]+)__END__/);
        if (!match) return false;
        const session = workspaceModule.sessions.get(item.sessionId); if (!session) return true;
        try {
            const result = JSON.parse(Buffer.from(match[1], 'base64').toString('utf8')); const data = result.data || {};
            session.mediaBrokerState = result.state || 'error';
            session.mediaBrokerError = result.state === 'error' ? (data.message || 'Media device enumeration failed.') : null;
            session.mediaDevices = { cameras: data.cameras || [], microphones: data.microphones || [], speakers: data.speakers || [] };
            session.mediaPolicy = { captureEnabled: false, recordingEnabled: false, liveStreamingEnabled: false, visibleIndicatorRequired: true };
        } catch (error) { session.mediaBrokerState = 'error'; session.mediaBrokerError = error.message; }
        delete outputs[responseId]; return true;
    }

    function captureAgentData(command, agent) {
        if (!command || command.action !== 'msg') return;
        if (command.type === 'runcommands' && typeof command.responseid === 'string' && command.responseid.indexOf('workspace-media-devices-') === 0) { consume(command.responseid, command.result); return; }
        if (command.type === 'console' && agent && agent.dbNodeKey && typeof command.value === 'string') { const responseId = pendingByNode[agent.dbNodeKey]; if (responseId && consume(responseId, command.value)) delete pendingByNode[agent.dbNodeKey]; }
    }

    return { list, captureAgentData };
};