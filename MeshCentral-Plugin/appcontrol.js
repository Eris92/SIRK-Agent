'use strict';

const crypto = require('crypto');

module.exports.createAppControl = function createAppControl(parent, workspaceModule) {
    const outputs = Object.create(null);
    const pendingByNode = Object.create(null);

    function makeId() { return crypto.randomBytes(12).toString('hex'); }
    function userId(user) { return String(user && user._id || ''); }
    function escapePowerShell(value) { return String(value == null ? '' : value).replace(/'/g, "''"); }
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
    function desktopFor(session) { return session.slot === 'admin1' ? 'SirK-Admin-1' : (session.slot === 'admin2' ? 'SirK-Admin-2' : 'default'); }

    const nativeSource = String.raw`
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SirKWorkspaceDesktop {
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr OpenWindowStation(string name, bool inherit, uint access);
  [DllImport("user32.dll", SetLastError=true)] static extern bool SetProcessWindowStation(IntPtr station);
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
  [DllImport("user32.dll", SetLastError=true)] static extern bool EnumDesktopWindows(IntPtr desktop, EnumProc callback, IntPtr param);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("wtsapi32.dll", SetLastError=true)] static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
  [DllImport("advapi32.dll", SetLastError=true)] static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int level, int type, out IntPtr token);
  [DllImport("userenv.dll", SetLastError=true)] static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);
  [DllImport("userenv.dll")] static extern bool DestroyEnvironmentBlock(IntPtr environment);
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool CreateProcessAsUser(IntPtr token, string app, StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr environment, string cwd, ref STARTUPINFO startup, out PROCESS_INFORMATION process);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
  delegate bool EnumProc(IntPtr hwnd, IntPtr param);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct STARTUPINFO { public int cb; public string reserved; public string desktop; public string title; public int x,y,xSize,ySize,xCount,yCount,fill; public int flags; public short show; public short reserved2; public IntPtr reservedPtr,input,output,error; }
  [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process, thread; public int processId, threadId; }
  const uint WINSTA_ALL_ACCESS=0x37F; const uint DESKTOP_ENUMERATE=0x40, DESKTOP_READOBJECTS=0x1;
  public static string[] List(string desktopName) {
    IntPtr station=OpenWindowStation("winsta0",false,WINSTA_ALL_ACCESS); if(station==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    if(!SetProcessWindowStation(station)) throw new System.ComponentModel.Win32Exception();
    IntPtr desktop=OpenDesktop(desktopName,0,false,DESKTOP_ENUMERATE|DESKTOP_READOBJECTS); if(desktop==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    var values=new List<string>(); EnumDesktopWindows(desktop,(h,p)=>{ var b=new StringBuilder(1024); int n=GetWindowText(h,b,b.Capacity); if(n>0 && IsWindowVisible(h)) values.Add(b.ToString()); return true;},IntPtr.Zero); return values.ToArray();
  }
  public static int Launch(uint sessionId,string desktopName,string file,string arguments) {
    IntPtr user,primary=IntPtr.Zero,environment=IntPtr.Zero; if(!WTSQueryUserToken(sessionId,out user)) throw new System.ComponentModel.Win32Exception();
    try { if(!DuplicateTokenEx(user,0xF01FF,IntPtr.Zero,2,1,out primary)) throw new System.ComponentModel.Win32Exception(); CreateEnvironmentBlock(out environment,primary,false);
      var si=new STARTUPINFO(); si.cb=Marshal.SizeOf(si); si.desktop="winsta0\\"+desktopName; var pi=new PROCESS_INFORMATION(); string cmd="\""+file+"\""+(String.IsNullOrWhiteSpace(arguments)?"":" "+arguments); var sb=new StringBuilder(cmd);
      if(!CreateProcessAsUser(primary,file,sb,IntPtr.Zero,IntPtr.Zero,false,0x400|0x10,environment,System.IO.Path.GetDirectoryName(file),ref si,out pi)) throw new System.ComponentModel.Win32Exception();
      CloseHandle(pi.thread); CloseHandle(pi.process); return pi.processId;
    } finally { if(environment!=IntPtr.Zero) DestroyEnvironmentBlock(environment); if(primary!=IntPtr.Zero) CloseHandle(primary); CloseHandle(user); }
  }
}`;
    const nativeBase64 = Buffer.from(nativeSource, 'utf8').toString('base64');

    function resultLine(sessionId, state, dataExpression) {
        return "$r=[ordered]@{sessionId='" + escapePowerShell(sessionId) + "';state='" + escapePowerShell(state) + "';data=" + dataExpression + "};Write-Output ('__WORKSPACE_APP_RESULT__'+($r|ConvertTo-Json -Compress -Depth 8))";
    }
    function prelude() { return "$src=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + nativeBase64 + "'));if(-not ('SirKWorkspaceDesktop' -as [type])){Add-Type -TypeDefinition $src -Language CSharp}"; }
    function listCommand(session) {
        const desktop = escapePowerShell(desktopFor(session));
        return ["$ErrorActionPreference='Stop'", 'try{', prelude(), "$items=[SirKWorkspaceDesktop]::List('" + desktop + "')", resultLine(session.id, 'listed', "([ordered]@{desktop='" + desktop + "';windows=@($items)})"), '}catch{' + resultLine(session.id, 'error', "([ordered]@{message=$_.Exception.Message})") + '}'].join(';');
    }
    function launchCommand(session, file, args) {
        const desktop = escapePowerShell(desktopFor(session));
        const safeFile = escapePowerShell(file); const safeArgs = escapePowerShell(args);
        return ["$ErrorActionPreference='Stop'", 'try{', prelude(), "$pid=[SirKWorkspaceDesktop]::Launch(" + Number(session.windowsSessionId || 0) + ",'" + desktop + "','" + safeFile + "','" + safeArgs + "')", resultLine(session.id, 'launched', "([ordered]@{desktop='" + desktop + "';file='" + safeFile + "';pid=$pid})"), '}catch{' + resultLine(session.id, 'error', "([ordered]@{message=$_.Exception.Message})") + '}'].join(';');
    }

    function dispatch(session, user, commandText, responseId) {
        return new Promise(function (resolve, reject) {
            const webServer = getWebServer(); const domain = getDomain(user);
            if (!webServer || !domain || typeof webServer.GetNodeWithRights !== 'function') return reject(new Error('MeshCentral device API is unavailable.'));
            const nodeId = normalizeNodeId(session.nodeId, domain);
            webServer.GetNodeWithRights(domain, user, nodeId, function (node, rights, visible) {
                if (!node || rights === 0 || visible === false) return reject(new Error('You do not have access to this device.'));
                if (((rights & 24) !== 24) && ((rights & 0x00020000) === 0)) return reject(new Error('You do not have permission to run commands on this device.'));
                const command = { action:'runcommands', type:2, cmds:commandText, runAsUser:2, sessionid:session.id, reply:true, responseid:responseId };
                const agents = webServer.wsagents || webServer.parent && webServer.parent.wsagents || parent.parent && parent.parent.wsagents || {}; const agent = agents[nodeId];
                outputs[responseId] = { output:'', sessionId:session.id }; pendingByNode[nodeId] = responseId;
                if (agent && agent.authenticated === 2 && agent.agentInfo) { try { agent.send(JSON.stringify(command)); return resolve(session); } catch (error) { return reject(error); } }
                const multiServer = webServer.multiServer || webServer.parent && webServer.parent.multiServer || parent.parent && parent.parent.multiServer;
                if (multiServer) { try { multiServer.DispatchMessage({ action:'agentCommand', nodeid:nodeId, command }); return resolve(session); } catch (error) { return reject(error); } }
                reject(new Error('Device agent is not connected.'));
            });
        });
    }

    function list(user, sessionId) { const session = getSession(user, sessionId); session.appsState='loading'; session.appsError=null; return dispatch(session,user,listCommand(session),'workspace-app-'+makeId()); }
    function launch(user, sessionId, file, args) {
        const session=getSession(user,sessionId); const value=String(file||'').trim(); if(!value) throw new Error('Application path is required.');
        const allowed=['explorer.exe','notepad.exe','cmd.exe','powershell.exe','pwsh.exe','taskmgr.exe','control.exe','mmc.exe'];
        const base=value.toLowerCase().split('\\').pop(); if(value.indexOf('\\')<0 && allowed.indexOf(base)<0) throw new Error('Use an absolute path or an allowed Windows application.');
        session.appsState='launching'; session.appsError=null; return dispatch(session,user,launchCommand(session,value,String(args||'')),'workspace-app-'+makeId());
    }
    function consume(responseId, raw) {
        const item=outputs[responseId]; if(!item) return false; item.output=(item.output+String(raw==null?'':raw)).slice(-1024*1024);
        const match=item.output.match(/__WORKSPACE_APP_RESULT__(\{[^\r\n]+\})/); if(!match) return false; const session=workspaceModule.sessions.get(item.sessionId); if(!session) return true;
        try { const result=JSON.parse(match[1]); const data=result.data||{}; session.appsState=result.state; session.appsError=result.state==='error'?(data.message||'Application operation failed.'):null; if(Array.isArray(data.windows)) session.windows=data.windows; if(data.pid) session.lastLaunchedPid=data.pid; if(data.file) session.lastLaunchedFile=data.file; }
        catch(error){ session.appsState='error'; session.appsError=error.message; }
        delete outputs[responseId]; return true;
    }
    function captureAgentData(command, agent) {
        if(!command || command.action!=='msg') return;
        if(command.type==='runcommands' && typeof command.responseid==='string' && command.responseid.indexOf('workspace-app-')===0){ consume(command.responseid,command.result); return; }
        if(command.type==='console' && agent && agent.dbNodeKey && typeof command.value==='string'){ const responseId=pendingByNode[agent.dbNodeKey]; if(responseId && consume(responseId,command.value)) delete pendingByNode[agent.dbNodeKey]; }
    }
    return { list, launch, captureAgentData };
};