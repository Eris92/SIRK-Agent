'use strict';

const crypto = require('crypto');

module.exports.createModule = function createModule(parent) {
    const sessions = new Map();
    const slots = new Map();
    const outputs = Object.create(null);
    const pendingByNode = Object.create(null);
    const outputTimeoutMs = 90000;
    const slotDefinitions = [
        { id: 'user', label: 'User', kind: 'user' },
        { id: 'admin1', label: 'Admin 1', kind: 'admin' },
        { id: 'admin2', label: 'Admin 2', kind: 'admin' }
    ];

    function now() { return new Date().toISOString(); }
    function makeId() { return crypto.randomBytes(16).toString('hex'); }
    function userId(user) { return String(user && user._id || ''); }
    function userName(user) { return String(user && (user.name || user.realname || user._id) || 'unknown'); }
    function slotKey(nodeId, slot) { return String(nodeId) + '|' + slot; }
    function slotDefinition(slot) { return slotDefinitions.find(function (item) { return item.id === slot; }) || null; }
    function isTerminal(state) { return state === 'stopped' || state === 'error'; }

    function getWebServer() {
        return (parent && parent.parent && parent.parent.webserver) || (parent && parent.webServer) || (parent && parent.parent) || null;
    }

    function getDomain(user) {
        const webServer = getWebServer();
        const domainId = userId(user).split('/')[1] || '';
        return webServer && webServer.domains && webServer.domains[domainId] ||
            parent.parent && parent.parent.config && parent.parent.config.domains && parent.parent.config.domains[domainId] || { id: domainId };
    }

    function createSession(nodeId, user, slot) {
        const definition = slotDefinition(slot);
        const session = {
            id: makeId(), nodeId, slot, slotLabel: definition.label, kind: definition.kind,
            ownerId: userId(user), ownerName: userName(user),
            state: 'requested', createdAt: now(), updatedAt: now(),
            pid: null, bootstrapPid: null, windowsSessionId: null, user: null, desktop: null,
            version: null, uptimeSeconds: null, lastHeartbeat: null,
            monitorCount: null, primaryWidth: null, primaryHeight: null,
            virtualWidth: null, virtualHeight: null,
            testWindowReady: null, testWindowThreadId: null, testWindowTitle: null,
            error: null, responseId: null
        };
        sessions.set(session.id, session);
        slots.set(slotKey(nodeId, slot), session.id);
        return session;
    }

    function updateSession(id, patch) {
        const session = sessions.get(id);
        if (!session) return null;
        Object.assign(session, patch, { updatedAt: now() });
        if (isTerminal(session.state)) {
            const key = slotKey(session.nodeId, session.slot);
            if (slots.get(key) === session.id) slots.delete(key);
        }
        return session;
    }

    function escapePowerShell(value) { return String(value == null ? '' : value).replace(/'/g, "''"); }
    function resultLine(sessionId, stateExpression, dataExpression) {
        return "$r=[ordered]@{sessionId='" + escapePowerShell(sessionId) + "';state=" + stateExpression + ";data=" + dataExpression + "};Write-Output ('__WORKSPACE_RESULT__'+($r|ConvertTo-Json -Compress -Depth 6))";
    }

    function launcherCommand(sessionId, slot) {
        const releaseBase = 'https://github.com/Eris92/MeshCentral-Workspace/releases/download/develop-latest';
        const success = resultLine(sessionId, "'running'", '$heartbeat');
        const failure = resultLine(sessionId, "'error'", "([ordered]@{message=$_.Exception.Message;type=$_.Exception.GetType().FullName;scriptStack=$_.ScriptStackTrace})");
        const safeSlot = escapePowerShell(slot);
        return [
            "$ErrorActionPreference='Stop'", "$ProgressPreference='SilentlyContinue'", 'try{',
            "$dir=Join-Path $env:ProgramData 'SirK\\Workspace'", "New-Item -Path $dir -ItemType Directory -Force|Out-Null",
            "$exe=Join-Path $dir 'WorkspaceHost.exe'", "$tmp=Join-Path $dir 'WorkspaceHost.exe.download'", "$shaFile=Join-Path $dir 'WorkspaceHost.exe.sha256'",
            "Invoke-WebRequest -UseBasicParsing -Uri '" + releaseBase + "/WorkspaceHost.exe' -OutFile $tmp",
            "Invoke-WebRequest -UseBasicParsing -Uri '" + releaseBase + "/WorkspaceHost.exe.sha256' -OutFile $shaFile",
            "$expected=((Get-Content $shaFile -Raw).Trim().Split(' ')[0]).ToUpperInvariant()", "$actual=(Get-FileHash $tmp -Algorithm SHA256).Hash.ToUpperInvariant()",
            "if($expected -ne $actual){throw 'WorkspaceHost SHA256 mismatch'}", "Move-Item $tmp $exe -Force", "Unblock-File $exe -ErrorAction SilentlyContinue",
            "$process=Start-Process -FilePath $exe -ArgumentList @('--slot','" + safeSlot + "') -PassThru -WindowStyle Hidden",
            "$pipe=[System.IO.Pipes.NamedPipeClientStream]::new('.','SirK.MeshCentral.Workspace." + safeSlot + "',[System.IO.Pipes.PipeDirection]::In,[System.IO.Pipes.PipeOptions]::None)",
            "try{$pipe.Connect(30000);$reader=[System.IO.StreamReader]::new($pipe,[System.Text.Encoding]::UTF8);try{$task=$reader.ReadLineAsync();if(-not $task.Wait([TimeSpan]::FromSeconds(30))){throw 'WorkspaceHost worker heartbeat timeout'};$line=$task.Result;if([string]::IsNullOrWhiteSpace($line)){throw 'WorkspaceHost returned empty heartbeat'};$heartbeat=$line|ConvertFrom-Json;if($heartbeat.type -ne 'heartbeat'){throw ('Unexpected heartbeat type: '+$heartbeat.type)};if($heartbeat.slot -ne '" + safeSlot + "'){throw ('Unexpected workspace slot: '+$heartbeat.slot)};$heartbeat|Add-Member -NotePropertyName bootstrapPid -NotePropertyValue $process.Id -Force;" + success + "}finally{$reader.Dispose()}}finally{$pipe.Dispose()}",
            '}catch{' + failure + '}'
        ].join(';');
    }

    function stopCommand(sessionId, pid) {
        const success = resultLine(sessionId, "'stopped'", "([ordered]@{pid=" + Number(pid || 0) + '})');
        const failure = resultLine(sessionId, "'error'", "([ordered]@{message=$_.Exception.Message})");
        return "$ErrorActionPreference='Stop';try{$p=Get-Process -Id " + Number(pid || 0) + " -ErrorAction SilentlyContinue;if($p){$p|Stop-Process -Force};" + success + '}catch{' + failure + '}';
    }

    function normalizeNodeId(nodeId, domain) {
        let value = String(nodeId || '');
        if (value.indexOf('/') < 0) value = 'node/' + domain.id + '/' + value;
        return value;
    }

    function dispatchCommand(session, user, commandText, responseId, callback) {
        const webServer = getWebServer();
        const domain = getDomain(user);
        if (!webServer || !domain || typeof webServer.GetNodeWithRights !== 'function') return callback('MeshCentral device API is unavailable.');
        const nodeId = normalizeNodeId(session.nodeId, domain);
        if (nodeId.split('/').length !== 3 || nodeId.split('/')[1] !== domain.id) return callback('Invalid device identifier.');
        webServer.GetNodeWithRights(domain, user, nodeId, function (node, rights, visible) {
            if (!node || rights === 0 || visible === false) return callback('You do not have access to this device.');
            if (((rights & 24) !== 24) && ((rights & 0x00020000) === 0)) return callback('You do not have permission to run commands on this device.');
            if (!node.agent || node.agent.id == null) return callback('Device agent information is unavailable.');
            const agentCommand = { action: 'runcommands', type: 2, cmds: commandText, runAsUser: 2, sessionid: session.id, reply: true, responseid: responseId };
            const agents = webServer.wsagents || webServer.parent && webServer.parent.wsagents || parent.parent && parent.parent.wsagents || {};
            const agent = agents[nodeId];
            outputs[responseId] = { ready: false, output: '', updatedAt: Date.now(), sessionId: session.id };
            pendingByNode[nodeId] = { responseId, buffer: '', updatedAt: Date.now() };
            updateSession(session.id, { nodeId, responseId, error: null });
            if (agent && agent.authenticated === 2 && agent.agentInfo) {
                try { agent.send(JSON.stringify(agentCommand)); return callback(null, session); } catch (error) { return callback('Could not send command: ' + error.message); }
            }
            const multiServer = webServer.multiServer || webServer.parent && webServer.parent.multiServer || parent.parent && parent.parent.multiServer;
            if (multiServer) {
                try { multiServer.DispatchMessage({ action: 'agentCommand', nodeid: nodeId, command: agentCommand }); return callback(null, session); } catch (error) { return callback('Could not route command: ' + error.message); }
            }
            callback('Device agent is not connected.');
        });
    }

    function consumeOutput(responseId, raw) {
        const item = outputs[responseId];
        if (!item) return;
        item.output = String(raw == null ? '' : raw).slice(0, 1024 * 1024);
        const session = sessions.get(item.sessionId);
        if (!session) return;
        const marker = item.output.match(/__WORKSPACE_RESULT__(\{[^\r\n]+\})/);
        if (!marker) { updateSession(session.id, { state: 'error', error: item.output.trim().slice(-2000) || 'MeshAgent did not return WorkspaceHost status.' }); return; }
        try {
            const result = JSON.parse(marker[1]);
            const data = result.data || {};
            if (result.state === 'error') { updateSession(session.id, { state: 'error', error: data.message || 'WorkspaceHost startup failed.' }); return; }
            updateSession(session.id, {
                state: result.state || 'running', pid: data.pid || session.pid || null, bootstrapPid: data.bootstrapPid || session.bootstrapPid || null,
                windowsSessionId: data.sessionId == null ? session.windowsSessionId : data.sessionId, user: data.user || session.user || null,
                version: data.version || session.version || null, desktop: data.desktop || session.desktop || null,
                uptimeSeconds: data.uptimeSeconds == null ? session.uptimeSeconds : data.uptimeSeconds,
                monitorCount: data.monitorCount == null ? session.monitorCount : data.monitorCount,
                primaryWidth: data.primaryWidth == null ? session.primaryWidth : data.primaryWidth,
                primaryHeight: data.primaryHeight == null ? session.primaryHeight : data.primaryHeight,
                virtualWidth: data.virtualWidth == null ? session.virtualWidth : data.virtualWidth,
                virtualHeight: data.virtualHeight == null ? session.virtualHeight : data.virtualHeight,
                testWindowReady: data.testWindowReady == null ? session.testWindowReady : data.testWindowReady,
                testWindowThreadId: data.testWindowThreadId == null ? session.testWindowThreadId : data.testWindowThreadId,
                testWindowTitle: data.testWindowTitle || session.testWindowTitle || null,
                lastHeartbeat: result.state === 'running' ? now() : session.lastHeartbeat, error: null
            });
        } catch (error) { updateSession(session.id, { state: 'error', error: 'Invalid WorkspaceHost result: ' + error.message }); }
    }

    function captureAgentData(command, agent) {
        if (!command || command.action !== 'msg') return;
        if (command.type === 'runcommands' && typeof command.responseid === 'string' && command.responseid.indexOf('workspace-') === 0) { consumeOutput(command.responseid, command.result); return; }
        if (command.type !== 'console' || !agent || !agent.dbNodeKey || typeof command.value !== 'string') return;
        const pending = pendingByNode[agent.dbNodeKey];
        if (!pending) return;
        pending.buffer = (pending.buffer + command.value).slice(-1024 * 1024);
        if (pending.buffer.indexOf('__WORKSPACE_RESULT__') >= 0 || pending.buffer.indexOf("Run commands can't execute, already busy.") >= 0) {
            consumeOutput(pending.responseId, pending.buffer); delete pendingByNode[agent.dbNodeKey];
        }
    }

    function start(user, nodeId, slot) {
        return new Promise(function (resolve, reject) {
            const definition = slotDefinition(String(slot || ''));
            if (!definition) { reject(new Error('Unknown workspace slot.')); return; }
            const key = slotKey(nodeId, definition.id);
            const activeId = slots.get(key);
            const active = activeId && sessions.get(activeId);
            if (active && !isTerminal(active.state)) {
                if (active.ownerId === userId(user)) { resolve(active); return; }
                reject(new Error(definition.label + ' is occupied by ' + active.ownerName + '.')); return;
            }
            const session = createSession(nodeId, user, definition.id);
            updateSession(session.id, { state: 'deploying' });
            dispatchCommand(session, user, launcherCommand(session.id, definition.id), 'workspace-start-' + session.id, function (error) {
                if (error) { updateSession(session.id, { state: 'error', error }); reject(new Error(error)); return; }
                resolve(session);
            });
        });
    }

    function stop(user, id) {
        return new Promise(function (resolve, reject) {
            const session = sessions.get(String(id || ''));
            if (!session) { reject(new Error('Session not found.')); return; }
            if (session.ownerId && session.ownerId !== userId(user) && user.siteadmin !== 0xFFFFFFFF) { reject(new Error('Workspace belongs to ' + session.ownerName + '.')); return; }
            if (!session.pid) { updateSession(session.id, { state: 'stopped' }); resolve(session); return; }
            updateSession(session.id, { state: 'stopping' });
            dispatchCommand(session, user, stopCommand(session.id, session.pid), 'workspace-stop-' + session.id, function (error) {
                if (error) { updateSession(session.id, { state: 'error', error }); reject(new Error(error)); return; }
                resolve(session);
            });
        });
    }

    function status(user, id) {
        const session = sessions.get(String(id || ''));
        if (!session) return null;
        if ((session.state === 'deploying' || session.state === 'stopping') && Date.now() - new Date(session.updatedAt).getTime() > outputTimeoutMs) updateSession(session.id, { state: 'error', error: 'Timed out waiting for MeshAgent result.' });
        return session;
    }

    function list(nodeId) {
        return slotDefinitions.map(function (definition) {
            const id = slots.get(slotKey(nodeId, definition.id));
            const session = id && sessions.get(id);
            return session || { slot: definition.id, slotLabel: definition.label, kind: definition.kind, state: 'free', ownerId: null, ownerName: null };
        });
    }

    return { sessions, start, stop, status, list, captureAgentData };
};