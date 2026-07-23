'use strict';

const crypto = require('crypto');

module.exports.createModule = function createModule(parent) {
    const sessions = new Map();
    const outputs = Object.create(null);
    const pendingByNode = Object.create(null);
    const outputTimeoutMs = 120000;

    function now() { return new Date().toISOString(); }
    function makeId() { return crypto.randomBytes(16).toString('hex'); }

    function getWebServer() {
        return (parent && parent.parent && parent.parent.webserver) ||
            (parent && parent.webServer) ||
            (parent && parent.parent) || null;
    }

    function getDomain(user) {
        const webServer = getWebServer();
        const domainId = String(user && user._id || '').split('/')[1] || '';
        return webServer && webServer.domains && webServer.domains[domainId] ||
            parent.parent && parent.parent.config && parent.parent.config.domains && parent.parent.config.domains[domainId] ||
            { id: domainId };
    }

    function createSession(nodeId, userId) {
        const session = {
            id: makeId(), nodeId, userId: userId || null,
            state: 'requested', createdAt: now(), updatedAt: now(),
            pid: null, windowsSessionId: null, user: null, desktop: null,
            version: null, lastHeartbeat: null, error: null, responseId: null
        };
        sessions.set(session.id, session);
        return session;
    }

    function updateSession(id, patch) {
        const session = sessions.get(id);
        if (!session) return null;
        Object.assign(session, patch, { updatedAt: now() });
        return session;
    }

    function escapePowerShell(value) { return String(value == null ? '' : value).replace(/'/g, "''"); }

    function launcherCommand(sessionId) {
        const releaseBase = 'https://github.com/Eris92/MeshCentral-Workspace/releases/download/develop-latest';
        return [
            "$ErrorActionPreference='Stop'",
            "$ProgressPreference='SilentlyContinue'",
            "$dir=Join-Path $env:LOCALAPPDATA 'SirK\\Workspace'",
            "New-Item -Path $dir -ItemType Directory -Force | Out-Null",
            "$exe=Join-Path $dir 'WorkspaceHost.exe'",
            "$tmp=Join-Path $dir 'WorkspaceHost.exe.download'",
            "$shaFile=Join-Path $dir 'WorkspaceHost.exe.sha256'",
            "Invoke-WebRequest -UseBasicParsing -Uri '" + releaseBase + "/WorkspaceHost.exe' -OutFile $tmp",
            "Invoke-WebRequest -UseBasicParsing -Uri '" + releaseBase + "/WorkspaceHost.exe.sha256' -OutFile $shaFile",
            "$expected=((Get-Content $shaFile -Raw).Trim().Split(' ')[0]).ToUpperInvariant()",
            "$actual=(Get-FileHash $tmp -Algorithm SHA256).Hash.ToUpperInvariant()",
            "if($expected -ne $actual){throw 'WorkspaceHost SHA256 mismatch'}",
            "Get-Process WorkspaceHost -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue",
            "Move-Item $tmp $exe -Force",
            "Unblock-File $exe -ErrorAction SilentlyContinue",
            "$process=Start-Process -FilePath $exe -PassThru -WindowStyle Hidden",
            "Start-Sleep -Seconds 2",
            "$running=Get-Process -Id $process.Id -ErrorAction Stop",
            "$result=[ordered]@{sessionId='" + escapePowerShell(sessionId) + "';state='running';pid=$running.Id;user=$env:USERNAME;version='0.2.0';path=$exe}",
            "Write-Output ('__WORKSPACE_RESULT__'+($result|ConvertTo-Json -Compress))"
        ].join(';');
    }

    function normalizeNodeId(nodeId, domain) {
        let value = String(nodeId || '');
        if (value.indexOf('/') < 0) value = 'node/' + domain.id + '/' + value;
        return value;
    }

    function sendToAgent(session, user, callback) {
        const webServer = getWebServer();
        const domain = getDomain(user);
        if (!webServer || !domain || typeof webServer.GetNodeWithRights !== 'function') return callback('MeshCentral device API is unavailable.');
        const nodeId = normalizeNodeId(session.nodeId, domain);
        if (nodeId.split('/').length !== 3 || nodeId.split('/')[1] !== domain.id) return callback('Invalid device identifier.');

        webServer.GetNodeWithRights(domain, user, nodeId, function (node, rights, visible) {
            if (!node || rights === 0 || visible === false) return callback('You do not have access to this device.');
            if (((rights & 24) !== 24) && ((rights & 0x00020000) === 0)) return callback('You do not have permission to run commands on this device.');
            if (!node.agent || node.agent.id == null) return callback('Device agent information is unavailable.');

            const responseId = 'workspace-' + session.id;
            const agentCommand = {
                action: 'runcommands', type: 2, cmds: launcherCommand(session.id),
                runAsUser: 2, sessionid: session.id, reply: true, responseid: responseId
            };
            const agents = webServer.wsagents || webServer.parent && webServer.parent.wsagents || parent.parent && parent.parent.wsagents || {};
            const agent = agents[nodeId];
            outputs[responseId] = { ready: false, output: '', updatedAt: Date.now(), sessionId: session.id };
            pendingByNode[nodeId] = { responseId, buffer: '', updatedAt: Date.now() };
            updateSession(session.id, { nodeId, responseId, state: 'deploying', error: null });

            if (agent && agent.authenticated === 2 && agent.agentInfo) {
                try { agent.send(JSON.stringify(agentCommand)); return callback(null, session); }
                catch (error) { return callback('Could not send command: ' + error.message); }
            }
            const multiServer = webServer.multiServer || webServer.parent && webServer.parent.multiServer || parent.parent && parent.parent.multiServer;
            if (multiServer) {
                try { multiServer.DispatchMessage({ action: 'agentCommand', nodeid: nodeId, command: agentCommand }); return callback(null, session); }
                catch (error) { return callback('Could not route command: ' + error.message); }
            }
            callback('Device agent is not connected.');
        });
    }

    function consumeOutput(responseId, raw) {
        const item = outputs[responseId];
        if (!item) return;
        item.output = String(raw == null ? '' : raw).slice(0, 1024 * 1024);
        item.ready = true;
        item.updatedAt = Date.now();
        const session = sessions.get(item.sessionId);
        if (!session) return;
        const marker = item.output.match(/__WORKSPACE_RESULT__(\{[^\r\n]+\})/);
        if (!marker) {
            updateSession(session.id, { state: 'error', error: item.output.trim().slice(-2000) || 'WorkspaceHost did not return status.' });
            return;
        }
        try {
            const data = JSON.parse(marker[1]);
            updateSession(session.id, {
                state: data.state || 'running', pid: data.pid || null,
                user: data.user || null, version: data.version || null,
                desktop: 'Default', lastHeartbeat: now(), error: null
            });
        } catch (error) { updateSession(session.id, { state: 'error', error: 'Invalid WorkspaceHost result: ' + error.message }); }
    }

    function captureAgentData(command, agent) {
        if (!command || command.action !== 'msg') return;
        if (command.type === 'runcommands' && typeof command.responseid === 'string' && command.responseid.indexOf('workspace-') === 0) {
            consumeOutput(command.responseid, command.result);
            return;
        }
        if (command.type !== 'console' || !agent || !agent.dbNodeKey || typeof command.value !== 'string') return;
        const pending = pendingByNode[agent.dbNodeKey];
        if (!pending) return;
        pending.buffer = (pending.buffer + command.value).slice(-1024 * 1024);
        pending.updatedAt = Date.now();
        if (pending.buffer.indexOf('__WORKSPACE_RESULT__') >= 0 || pending.buffer.indexOf("Run commands can't execute, already busy.") >= 0) {
            consumeOutput(pending.responseId, pending.buffer);
            delete pendingByNode[agent.dbNodeKey];
        }
    }

    function start(user, nodeId) {
        return new Promise(function (resolve, reject) {
            const session = createSession(nodeId, user && user._id);
            sendToAgent(session, user, function (error) {
                if (error) { updateSession(session.id, { state: 'error', error }); reject(new Error(error)); return; }
                resolve(session);
            });
        });
    }

    function stop(user, id) {
        const session = sessions.get(String(id || ''));
        if (!session) return Promise.reject(new Error('Session not found.'));
        updateSession(session.id, { state: 'stopped' });
        return Promise.resolve(session);
    }

    function status(user, id) {
        const session = sessions.get(String(id || ''));
        if (!session) return null;
        if (session.userId && user && session.userId !== user._id && user.siteadmin !== 0xFFFFFFFF) return null;
        if (session.state === 'deploying' && Date.now() - new Date(session.updatedAt).getTime() > outputTimeoutMs) updateSession(session.id, { state: 'error', error: 'Timed out waiting for MeshAgent result.' });
        return session;
    }

    return { sessions, start, stop, status, captureAgentData };
};
