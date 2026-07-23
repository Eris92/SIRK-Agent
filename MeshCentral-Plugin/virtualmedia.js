'use strict';

const crypto = require('crypto');

module.exports.createVirtualMedia = function createVirtualMedia(parent, workspaceModule) {
    const outputs = Object.create(null);
    const pendingByNode = Object.create(null);

    function makeId() { return crypto.randomBytes(12).toString('hex'); }
    function now() { return new Date().toISOString(); }
    function userId(user) { return String(user && user._id || ''); }
    function escapePowerShell(value) { return String(value == null ? '' : value).replace(/'/g, "''"); }

    function getWebServer() {
        return (parent && parent.parent && parent.parent.webserver) || (parent && parent.webServer) || (parent && parent.parent) || null;
    }

    function getDomain(user) {
        const webServer = getWebServer();
        const domainId = userId(user).split('/')[1] || '';
        return webServer && webServer.domains && webServer.domains[domainId] ||
            parent.parent && parent.parent.config && parent.parent.config.domains && parent.parent.config.domains[domainId] || { id: domainId };
    }

    function normalizeNodeId(nodeId, domain) {
        let value = String(nodeId || '');
        if (value.indexOf('/') < 0) value = 'node/' + domain.id + '/' + value;
        return value;
    }

    function getSession(user, sessionId) {
        const session = workspaceModule.sessions.get(String(sessionId || ''));
        if (!session) throw new Error('Workspace session not found.');
        if (session.ownerId && session.ownerId !== userId(user) && user.siteadmin !== 0xFFFFFFFF) throw new Error('Workspace belongs to ' + session.ownerName + '.');
        if (session.state !== 'running') throw new Error('Workspace must be running.');
        return session;
    }

    function resultLine(sessionId, state, dataExpression) {
        return "$r=[ordered]@{sessionId='" + escapePowerShell(sessionId) + "';state='" + escapePowerShell(state) + "';data=" + dataExpression + "};Write-Output ('__WORKSPACE_MEDIA_RESULT__'+($r|ConvertTo-Json -Compress -Depth 6))";
    }

    function mountCommand(session, url) {
        const safeUrl = escapePowerShell(url);
        const safeId = escapePowerShell(session.id);
        const success = resultLine(session.id, 'mounted', "([ordered]@{url=$url;path=$path;drive=$drive;sizeBytes=(Get-Item $path).Length})");
        const failure = resultLine(session.id, 'error', "([ordered]@{message=$_.Exception.Message})");
        return [
            "$ErrorActionPreference='Stop'", "$ProgressPreference='SilentlyContinue'", 'try{',
            "$dir=Join-Path $env:ProgramData 'SirK\\Workspace\\Media'", "New-Item -Path $dir -ItemType Directory -Force|Out-Null",
            "$url='" + safeUrl + "'", "$path=Join-Path $dir '" + safeId + ".iso'", "$tmp=$path+'.download'",
            "Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue|Where-Object Attached|Dismount-DiskImage -ErrorAction SilentlyContinue",
            "Remove-Item $tmp,$path -Force -ErrorAction SilentlyContinue",
            "Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $tmp",
            "if((Get-Item $tmp).Length -lt 1048576){throw 'Downloaded image is smaller than 1 MB.'}",
            "Move-Item $tmp $path -Force", "Mount-DiskImage -ImagePath $path -StorageType ISO|Out-Null",
            "$disk=Get-DiskImage -ImagePath $path", "$volume=$disk|Get-Volume|Select-Object -First 1", "$drive=if($volume.DriveLetter){$volume.DriveLetter+':'}else{$null}",
            success,
            '}catch{Remove-Item $tmp -Force -ErrorAction SilentlyContinue;' + failure + '}'
        ].join(';');
    }

    function unmountCommand(session) {
        const safeId = escapePowerShell(session.id);
        const success = resultLine(session.id, 'idle', "([ordered]@{path=$path})");
        const failure = resultLine(session.id, 'error', "([ordered]@{message=$_.Exception.Message})");
        return [
            "$ErrorActionPreference='Stop'", 'try{',
            "$path=Join-Path (Join-Path $env:ProgramData 'SirK\\Workspace\\Media') '" + safeId + ".iso'",
            "if(Test-Path $path){Get-DiskImage -ImagePath $path -ErrorAction SilentlyContinue|Where-Object Attached|Dismount-DiskImage -ErrorAction SilentlyContinue;Remove-Item $path -Force -ErrorAction SilentlyContinue}",
            success,
            '}catch{' + failure + '}'
        ].join(';');
    }

    function dispatch(session, user, commandText, responseId) {
        return new Promise(function (resolve, reject) {
            const webServer = getWebServer();
            const domain = getDomain(user);
            if (!webServer || !domain || typeof webServer.GetNodeWithRights !== 'function') { reject(new Error('MeshCentral device API is unavailable.')); return; }
            const nodeId = normalizeNodeId(session.nodeId, domain);
            webServer.GetNodeWithRights(domain, user, nodeId, function (node, rights, visible) {
                if (!node || rights === 0 || visible === false) { reject(new Error('You do not have access to this device.')); return; }
                if (((rights & 24) !== 24) && ((rights & 0x00020000) === 0)) { reject(new Error('You do not have permission to run commands on this device.')); return; }
                const command = { action: 'runcommands', type: 2, cmds: commandText, runAsUser: 2, sessionid: session.id, reply: true, responseid: responseId };
                const agents = webServer.wsagents || webServer.parent && webServer.parent.wsagents || parent.parent && parent.parent.wsagents || {};
                const agent = agents[nodeId];
                outputs[responseId] = { output: '', sessionId: session.id };
                pendingByNode[nodeId] = responseId;
                if (agent && agent.authenticated === 2 && agent.agentInfo) {
                    try { agent.send(JSON.stringify(command)); resolve(session); } catch (error) { reject(error); }
                    return;
                }
                const multiServer = webServer.multiServer || webServer.parent && webServer.parent.multiServer || parent.parent && parent.parent.multiServer;
                if (multiServer) {
                    try { multiServer.DispatchMessage({ action: 'agentCommand', nodeid: nodeId, command }); resolve(session); } catch (error) { reject(error); }
                    return;
                }
                reject(new Error('Device agent is not connected.'));
            });
        });
    }

    function mount(user, sessionId, url) {
        const session = getSession(user, sessionId);
        const value = String(url || '').trim();
        let parsed;
        try { parsed = new URL(value); } catch (error) { throw new Error('Enter a valid HTTPS URL to an ISO image.'); }
        if (parsed.protocol !== 'https:') throw new Error('Only HTTPS URLs are allowed.');
        session.virtualMediaState = 'mounting'; session.virtualMediaUrl = value; session.virtualMediaError = null; session.virtualMediaUpdatedAt = now();
        return dispatch(session, user, mountCommand(session, value), 'workspace-media-' + makeId());
    }

    function unmount(user, sessionId) {
        const session = getSession(user, sessionId);
        session.virtualMediaState = 'unmounting'; session.virtualMediaError = null; session.virtualMediaUpdatedAt = now();
        return dispatch(session, user, unmountCommand(session), 'workspace-media-' + makeId());
    }

    function consume(responseId, raw) {
        const item = outputs[responseId];
        if (!item) return false;
        item.output = (item.output + String(raw == null ? '' : raw)).slice(-1024 * 1024);
        const match = item.output.match(/__WORKSPACE_MEDIA_RESULT__(\{[^\r\n]+\})/);
        if (!match) return false;
        const session = workspaceModule.sessions.get(item.sessionId);
        if (!session) return true;
        try {
            const result = JSON.parse(match[1]);
            const data = result.data || {};
            session.virtualMediaState = result.state || 'error';
            session.virtualMediaError = result.state === 'error' ? (data.message || 'Virtual Media operation failed.') : null;
            session.virtualMediaPath = data.path || session.virtualMediaPath || null;
            session.virtualMediaDrive = data.drive || null;
            session.virtualMediaSizeBytes = data.sizeBytes == null ? session.virtualMediaSizeBytes || null : data.sizeBytes;
            if (result.state === 'idle') { session.virtualMediaUrl = null; session.virtualMediaPath = null; session.virtualMediaDrive = null; session.virtualMediaSizeBytes = null; }
            session.virtualMediaUpdatedAt = now();
        } catch (error) {
            session.virtualMediaState = 'error'; session.virtualMediaError = error.message; session.virtualMediaUpdatedAt = now();
        }
        delete outputs[responseId];
        return true;
    }

    function captureAgentData(command, agent) {
        if (!command || command.action !== 'msg') return;
        if (command.type === 'runcommands' && typeof command.responseid === 'string' && command.responseid.indexOf('workspace-media-') === 0) {
            consume(command.responseid, command.result);
            return;
        }
        if (command.type === 'console' && agent && agent.dbNodeKey && typeof command.value === 'string') {
            const responseId = pendingByNode[agent.dbNodeKey];
            if (responseId && consume(responseId, command.value)) delete pendingByNode[agent.dbNodeKey];
        }
    }

    return { mount, unmount, captureAgentData };
};
