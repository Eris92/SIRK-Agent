'use strict';

const crypto = require('crypto');

module.exports.createModule = function createModule(parent) {
    const sessions = new Map();
    const outputs = Object.create(null);
    const pendingByNode = Object.create(null);
    const outputTimeoutMs = 90000;

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
            version: null, uptimeSeconds: null, lastHeartbeat: null,
            error: null, responseId: null
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

    function resultLine(sessionId, stateExpression, dataExpression) {
        return "$r=[ordered]@{sessionId='" + escapePowerShell(sessionId) + "';state=" + stateExpression + ";data=" + dataExpression + "};Write-Output ('__WORKSPACE_RESULT__'+($r|ConvertTo-Json -Compress -Depth 6))";
    }

    function launcherCommand(sessionId) {
        const releaseBase = 'https://github.com/Eris92/MeshCentral-Workspace/releases/download/develop-latest';
        const success = resultLine(sessionId, "'running'", '$heartbeat');
        const failure = resultLine(sessionId, "'error'", "([ordered]@{message=$_.Exception.Message;type=$_.Exception.GetType().FullName;scriptStack=$_.ScriptStackTrace})");
        return [
            "$ErrorActionPreference='Stop'",
            "$ProgressPreference='SilentlyContinue'",
            "try{",
            "$dir=Join-Path $env:LOCALAPPDATA 'SirK\\Workspace'",
            "New-Item -Path $dir -ItemType Directory -Force|Out-Null",
            "$exe=Join-Path $dir 'WorkspaceHost.exe'",
            "$tmp=Join-Path $dir 'WorkspaceHost.exe.download'",
            "$shaFile=Join-Path $dir 'WorkspaceHost.exe.sha256'",
            "Invoke-WebRequest -UseBasicParsing -Uri '" + releaseBase + "/WorkspaceHost.exe' -OutFile $tmp",
            "Invoke-WebRequest -UseBasicParsing -Uri '" + releaseBase + "/WorkspaceHost.exe.sha256' -OutFile $shaFile",
            "$expected=((Get-Content $shaFile -Raw).Trim().Split(' ')[0]).ToUpperInvariant()",
            "$actual=(Get-FileHash $tmp -Algorithm SHA256).Hash.ToUpperInvariant()",
            "if($expected -ne $actual){throw 'WorkspaceHost SHA256 mismatch'}",
            "Get-Process WorkspaceHost -ErrorAction SilentlyContinue|Stop-Process -Force -ErrorAction SilentlyContinue",
            "Move-Item $tmp $exe -Force",
            "Unblock-File $exe -ErrorAction SilentlyContinue",
            "$process=Start-Process -FilePath $exe -PassThru -WindowStyle Hidden",
            "$pipe=[System.IO.Pipes.NamedPipeClientStream]::new('.','SirK.MeshCentral.Workspace',[System.IO.Pipes.PipeDirection]::In,[System.IO.Pipes.PipeOptions]::None)",
            "try{$pipe.Connect(20000);$reader=[System.IO.StreamReader]::new($pipe,[System.Text.Encoding]::UTF8);try{$task=$reader.ReadLineAsync();if(-not $task.Wait([TimeSpan]::FromSeconds(20))){throw 'WorkspaceHost heartbeat timeout'};$line=$task.Result;if([string]::IsNullOrWhiteSpace($line)){throw 'WorkspaceHost returned empty heartbeat'};$heartbeat=$line|ConvertFrom-Json;if($heartbeat.type -ne 'heartbeat'){throw ('Unexpected heartbeat type: '+$heartbeat.type)};if([int]$heartbeat.pid -ne [int]$process.Id){throw 'WorkspaceHost PID mismatch'};" + success + "}finally{$reader.Dispose()}}finally{$pipe.Dispose()}",
            "}catch{" + failure + "}"
        ].join(';');
    }

    function stopCommand(sessionId, pid) {
        const success = resultLine(sessionId, "'stopped'", "([ordered]@{pid=" + Number(pid || 0) + "})");
        const failure = resultLine(sessionId, "'error'", "([ordered]@{message=$_.Exception.Message})");
        return "$ErrorActionPreference='Stop';try{$p=Get-Process -Id " + Number(pid || 0) + " -ErrorAction SilentlyContinue;if($p){$p|Stop-Process -Force};" + success + "}catch{" + failure + "}";
    }

    function normalizeNodeId(nodeId, domain) {
        let value = String(nodeId || '');
        if (value.indexOf('/') < 0) value = 'node/' + domain.id + '/' + value;
        return value;
    }

    function dispatchCommand(session, user, commandText, responseId, runAsUser, callback) {
        const webServer = getWebServer();
        const domain = getDomain(user);
        if (!webServer || !domain || typeof webServer.GetNodeWithRights !== 'function') return callback('MeshCentral device API is unavailable.');
        const nodeId = normalizeNodeId(session.nodeId, domain);
        if (nodeId.split('/').length !== 3 || nodeId.split('/')[1] !== domain.id) return callback('Invalid device identifier.');

        webServer.GetNodeWithRights(domain, user, nodeId, function (node, rights, visible) {
            if (!node || rights === 0 || visible === false) return callback('You do not have access to this device.');
            if (((rights & 24) !== 24) && ((rights & 0x00020000) === 0)) return callback('You do not have permission to run commands on this device.');
            if (!node.agent || node.agent.id == null) return callback('Device agent information is unavailable.');

            const agentCommand = { action: 'runcommands', type: 2, cmds: commandText, runAsUser: runAsUser, sessionid: session.id, reply: true, responseid: responseId };
            const agents = webServer.wsagents || webServer.parent && webServer.parent.wsagents || parent.parent && parent.parent.wsagents || {};
            const agent = agents[nodeId];
            outputs[responseId] = { ready: false, output: '', updatedAt: Date.now(), sessionId: session.id };
            pendingByNode[nodeId] = { responseId, buffer: '', updatedAt: Date.now() };
            updateSession(session.id, { nodeId, responseId, error: null });

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
            updateSession(session.id, { state: 'error', error: item.output.trim().slice(-2000) || 'MeshAgent did not return WorkspaceHost status.' });
            return;
        }
        try {
            const result = JSON.parse(marker[1]);
            const data = result.data || {};
            if (result.state === 'error') {
                updateSession(session.id, { state: 'error', error: data.message || 'WorkspaceHost startup failed.' });
                return;
            }
            updateSession(session.id, {
                state: result.state || 'running',
                pid: data.pid || session.pid || null,
                windowsSessionId: data.sessionId == null ? session.windowsSessionId : data.sessionId,
                user: data.user || session.user || null,
                version: data.version || session.version || null,
                desktop: data.desktop || session.desktop || null,
                uptimeSeconds: data.uptimeSeconds == null ? session.uptimeSeconds : data.uptimeSeconds,
                lastHeartbeat: result.state === 'running' ? now() : session.lastHeartbeat,
                error: null
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
            updateSession(session.id, { state: 'deploying' });
            dispatchCommand(session, user, launcherCommand(session.id), 'workspace-start-' + session.id, 2, function (error) {
                if (error) { updateSession(session.id, { state: 'error', error }); reject(new Error(error)); return; }
                resolve(session);
            });
        });
    }

    function stop(user, id) {
        return new Promise(function (resolve, reject) {
            const session = sessions.get(String(id || ''));
            if (!session) { reject(new Error('Session not found.')); return; }
            if (session.userId && user && session.userId !== user._id && user.siteadmin !== 0xFFFFFFFF) { reject(new Error('Permission denied.')); return; }
            if (!session.pid) { updateSession(session.id, { state: 'stopped' }); resolve(session); return; }
            updateSession(session.id, { state: 'stopping' });
            dispatchCommand(session, user, stopCommand(session.id, session.pid), 'workspace-stop-' + session.id, 2, function (error) {
                if (error) { updateSession(session.id, { state: 'error', error }); reject(new Error(error)); return; }
                resolve(session);
            });
        });
    }

    function status(user, id) {
        const session = sessions.get(String(id || ''));
        if (!session) return null;
        if (session.userId && user && session.userId !== user._id && user.siteadmin !== 0xFFFFFFFF) return null;
        if ((session.state === 'deploying' || session.state === 'stopping') && Date.now() - new Date(session.updatedAt).getTime() > outputTimeoutMs) {
            updateSession(session.id, { state: 'error', error: 'Timed out waiting for MeshAgent result.' });
        }
        return session;
    }

    return { sessions, start, stop, status, captureAgentData };
};