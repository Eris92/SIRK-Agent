'use strict';

const crypto = require('crypto');

module.exports.createCaptureControl = function createCaptureControl(parent, workspaceModule) {
    const outputs = Object.create(null);
    const pendingByNode = Object.create(null);
    const images = new Map();
    const markerStart = '__WORKSPACE_DXGI_RESULT_B64__';
    const markerEnd = '__WORKSPACE_DXGI_RESULT_END__';
    const maxOutputBytes = 16 * 1024 * 1024;

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
        const safeId = String(sessionId).replace(/'/g, "''");
        return "$r=[ordered]@{sessionId='" + safeId + "';state='" + state + "';data=" + dataExpression + "};$j=$r|ConvertTo-Json -Compress -Depth 8;$b=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($j));Write-Output ('" + markerStart + "'+$b+'" + markerEnd + "')";
    }

    function captureCommand(session) {
        const releaseBase = 'https://github.com/Eris92/MeshCentral-Workspace/releases/download/develop-latest';
        const success = resultLine(session.id, 'ready', "([ordered]@{width=$meta.width;height=$meta.height;backend=$meta.backend;image=[Convert]::ToBase64String([IO.File]::ReadAllBytes($png))})");
        const failure = resultLine(session.id, 'error', "([ordered]@{message=$_.Exception.Message})");
        return [
            "$ErrorActionPreference='Stop'", "$ProgressPreference='SilentlyContinue'", 'try{',
            "$dir=Join-Path $env:ProgramData 'SirK\\Workspace'", "New-Item -Path $dir -ItemType Directory -Force|Out-Null",
            "$exe=Join-Path $dir 'WorkspaceCapture.exe'", "$shaFile=Join-Path $dir 'WorkspaceCapture.exe.sha256'", "$tmp=Join-Path $dir 'WorkspaceCapture.exe.download'",
            "$expectedVersion='0.1.0'", "$currentVersion=$null",
            "if(Test-Path $exe){try{$currentVersion=((& $exe --version 2>$null)|Select-Object -First 1).ToString().Trim()}catch{$currentVersion=$null}}",
            "if($currentVersion -ne $expectedVersion){Remove-Item $tmp,$shaFile -Force -ErrorAction SilentlyContinue;Invoke-WebRequest -UseBasicParsing -Uri '" + releaseBase + "/WorkspaceCapture.exe' -OutFile $tmp;Invoke-WebRequest -UseBasicParsing -Uri '" + releaseBase + "/WorkspaceCapture.exe.sha256' -OutFile $shaFile;$expectedHash=((Get-Content $shaFile -Raw).Trim().Split(' ')[0]).ToUpperInvariant();$actualHash=(Get-FileHash $tmp -Algorithm SHA256).Hash.ToUpperInvariant();if($expectedHash -ne $actualHash){throw 'WorkspaceCapture SHA256 mismatch'};Move-Item $tmp $exe -Force;Unblock-File $exe -ErrorAction SilentlyContinue}",
            "$png=Join-Path $dir ('capture-' + [guid]::NewGuid().ToString('N') + '.png')",
            "$output=& $exe --output $png 2>&1 | Out-String", "if($LASTEXITCODE -ne 0){throw ($output.Trim())}",
            "$meta=$output|ConvertFrom-Json", "if(-not(Test-Path $png)){throw 'WorkspaceCapture did not create a PNG file.'}",
            success, "Remove-Item $png -Force -ErrorAction SilentlyContinue",
            '}catch{' + failure + ';if($png){Remove-Item $png -Force -ErrorAction SilentlyContinue}}'
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
                const agent = agents[nodeId];
                outputs[responseId] = { output: '', sessionId: session.id };
                pendingByNode[nodeId] = responseId;
                if (agent && agent.authenticated === 2 && agent.agentInfo) { try { agent.send(JSON.stringify(command)); return resolve(session); } catch (error) { return reject(error); } }
                const multiServer = webServer.multiServer || webServer.parent && webServer.parent.multiServer || parent.parent && parent.parent.multiServer;
                if (multiServer) { try { multiServer.DispatchMessage({ action: 'agentCommand', nodeid: nodeId, command }); return resolve(session); } catch (error) { return reject(error); } }
                reject(new Error('Device agent is not connected.'));
            });
        });
    }

    function capture(user, sessionId) {
        const session = getSession(user, sessionId);
        if (session.slot !== 'user') throw new Error('DXGI capture currently supports only the visible user desktop. Hidden Workspace capture requires the virtual display backend.');
        session.captureState = 'capturing';
        session.captureError = null;
        session.captureUpdatedAt = new Date().toISOString();
        return dispatch(session, user, captureCommand(session), 'workspace-dxgi-' + makeId());
    }

    function consume(responseId, raw) {
        const item = outputs[responseId]; if (!item) return false;
        item.output = (item.output + String(raw == null ? '' : raw)).slice(-maxOutputBytes);
        const start = item.output.lastIndexOf(markerStart); if (start < 0) return false;
        const payloadStart = start + markerStart.length;
        const end = item.output.indexOf(markerEnd, payloadStart); if (end < 0) return false;
        const encoded = item.output.substring(payloadStart, end).replace(/\s+/g, '');
        const session = workspaceModule.sessions.get(item.sessionId);
        if (!session) { delete outputs[responseId]; return true; }
        try {
            const result = JSON.parse(Buffer.from(encoded, 'base64').toString('utf8'));
            const data = result.data || {};
            if (result.state === 'error') throw new Error(data.message || 'DXGI capture failed.');
            const image = Buffer.from(String(data.image || ''), 'base64');
            if (image.length < 100 || image.length > 12 * 1024 * 1024) throw new Error('Invalid capture image size.');
            images.set(session.id, { buffer: image, ownerId: session.ownerId, updatedAt: Date.now() });
            session.captureState = 'ready';
            session.captureError = null;
            session.captureWidth = data.width || null;
            session.captureHeight = data.height || null;
            session.captureBackend = data.backend || 'DXGI Desktop Duplication';
            session.captureVersion = Date.now();
            session.captureUpdatedAt = new Date().toISOString();
        } catch (error) {
            session.captureState = 'error';
            session.captureError = error.message;
            session.captureUpdatedAt = new Date().toISOString();
        }
        delete outputs[responseId];
        return true;
    }

    function captureAgentData(command, agent) {
        if (!command || command.action !== 'msg') return;
        if (command.type === 'runcommands' && typeof command.responseid === 'string' && command.responseid.indexOf('workspace-dxgi-') === 0) { consume(command.responseid, command.result); return; }
        if (command.type === 'console' && agent && agent.dbNodeKey && typeof command.value === 'string') {
            const responseId = pendingByNode[agent.dbNodeKey];
            if (responseId && consume(responseId, command.value)) delete pendingByNode[agent.dbNodeKey];
        }
    }

    function getImage(user, sessionId) {
        const session = getSession(user, sessionId);
        const image = images.get(session.id);
        if (!image) throw new Error('Capture image not found.');
        return image.buffer;
    }

    return { capture, getImage, captureAgentData };
};
