'use strict';

const crypto = require('crypto');

module.exports.createCaptureControl = function createCaptureControl(parent, workspaceModule) {
    const outputs = Object.create(null);
    const pendingByNode = Object.create(null);
    const images = new Map();
    const maxOutputBytes = 4 * 1024 * 1024;

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
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public static class SirKWorkspaceCapture092 {
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr OpenWindowStation(string name, bool inherit, uint access);
  [DllImport("user32.dll", SetLastError=true)] static extern bool SetProcessWindowStation(IntPtr station);
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
  [DllImport("user32.dll", SetLastError=true)] static extern bool SetThreadDesktop(IntPtr desktop);
  [DllImport("user32.dll", SetLastError=true)] static extern bool EnumDesktopWindows(IntPtr desktop, EnumProc callback, IntPtr param);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll", SetLastError=true)] static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
  [DllImport("user32.dll")] static extern bool CloseWindowStation(IntPtr station);
  delegate bool EnumProc(IntPtr hwnd, IntPtr param);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
  const uint WINSTA_ALL_ACCESS=0x37F;
  const uint DESKTOP_READOBJECTS=0x1, DESKTOP_ENUMERATE=0x40, DESKTOP_WRITEOBJECTS=0x80;
  const uint PW_RENDERFULLCONTENT=2;

  public sealed class Result {
    public string ImageBase64;
    public int Width;
    public int Height;
    public int WindowCount;
  }

  public static Result Capture(string desktopName, int sourceWidth, int sourceHeight, int maxWidth, int maxHeight) {
    Result result=null; Exception failure=null;
    var thread=new Thread(()=>{
      IntPtr station=IntPtr.Zero, desktop=IntPtr.Zero;
      try {
        station=OpenWindowStation("winsta0",false,WINSTA_ALL_ACCESS); if(station==IntPtr.Zero) throw new Win32Exception();
        if(!SetProcessWindowStation(station)) throw new Win32Exception();
        desktop=OpenDesktop(desktopName,0,false,DESKTOP_READOBJECTS|DESKTOP_ENUMERATE|DESKTOP_WRITEOBJECTS); if(desktop==IntPtr.Zero) throw new Win32Exception();
        if(!SetThreadDesktop(desktop)) throw new Win32Exception();
        int width=Math.Max(320,sourceWidth), height=Math.Max(200,sourceHeight), count=0;
        using(var canvas=new Bitmap(width,height,PixelFormat.Format24bppRgb)) {
          using(var graphics=Graphics.FromImage(canvas)) {
            graphics.Clear(Color.FromArgb(28,28,28));
            EnumDesktopWindows(desktop,(hwnd,param)=>{
              if(!IsWindowVisible(hwnd)) return true;
              RECT rect; if(!GetWindowRect(hwnd,out rect)) return true;
              int w=rect.Right-rect.Left, h=rect.Bottom-rect.Top; if(w<2 || h<2) return true;
              try {
                using(var windowBitmap=new Bitmap(w,h,PixelFormat.Format24bppRgb))
                using(var windowGraphics=Graphics.FromImage(windowBitmap)) {
                  IntPtr dc=windowGraphics.GetHdc();
                  bool ok=false; try { ok=PrintWindow(hwnd,dc,PW_RENDERFULLCONTENT); } finally { windowGraphics.ReleaseHdc(dc); }
                  if(ok) { graphics.DrawImageUnscaled(windowBitmap,rect.Left,rect.Top); count++; }
                }
              } catch { }
              return true;
            },IntPtr.Zero);
          }
          double scale=Math.Min(1.0,Math.Min((double)maxWidth/width,(double)maxHeight/height));
          int outWidth=Math.Max(1,(int)Math.Round(width*scale)), outHeight=Math.Max(1,(int)Math.Round(height*scale));
          using(var output=new Bitmap(outWidth,outHeight,PixelFormat.Format24bppRgb))
          using(var g=Graphics.FromImage(output))
          using(var stream=new MemoryStream()) {
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            g.DrawImage(canvas,new Rectangle(0,0,outWidth,outHeight));
            output.Save(stream,ImageFormat.Png);
            result=new Result { ImageBase64=Convert.ToBase64String(stream.ToArray()), Width=outWidth, Height=outHeight, WindowCount=count };
          }
        }
      } catch(Exception ex) { failure=ex; }
      finally { if(desktop!=IntPtr.Zero) CloseDesktop(desktop); if(station!=IntPtr.Zero) CloseWindowStation(station); }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    if(failure!=null) throw failure; return result;
  }
}`;
    const nativeBase64 = Buffer.from(nativeSource, 'utf8').toString('base64');

    function resultLine(sessionId, state, dataExpression) {
        return "$r=[ordered]@{sessionId='" + escapePowerShell(sessionId) + "';state='" + escapePowerShell(state) + "';data=" + dataExpression + "};$j=$r|ConvertTo-Json -Compress -Depth 8;$b=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($j));Write-Output ('__WORKSPACE_CAPTURE_RESULT_B64__'+$b+'__WORKSPACE_CAPTURE_END__')";
    }
    function captureCommand(session) {
        const desktop = escapePowerShell(desktopFor(session));
        const width = Math.max(320, Number(session.virtualWidth || session.primaryWidth || 1280));
        const height = Math.max(200, Number(session.virtualHeight || session.primaryHeight || 720));
        const prelude = "$src=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + nativeBase64 + "'));Add-Type -AssemblyName System.Drawing;if(-not ('SirKWorkspaceCapture092' -as [type])){Add-Type -TypeDefinition $src -Language CSharp -ReferencedAssemblies System.Drawing}";
        return [
            "$ErrorActionPreference='Stop'", 'try{', prelude(),
            "$capture=[SirKWorkspaceCapture092]::Capture('" + desktop + "'," + width + ',' + height + ',1280,720)',
            resultLine(session.id, 'ready', "([ordered]@{desktop='" + desktop + "';width=$capture.Width;height=$capture.Height;windowCount=$capture.WindowCount;image=$capture.ImageBase64})"),
            '}catch{' + resultLine(session.id, 'error', "([ordered]@{message=$_.Exception.Message})") + '}'
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

    function capture(user, sessionId) {
        const session = getSession(user, sessionId);
        session.captureState = 'capturing'; session.captureError = null; session.captureUpdatedAt = new Date().toISOString();
        return dispatch(session, user, captureCommand(session), 'workspace-capture-' + makeId());
    }

    function consume(responseId, raw) {
        const item = outputs[responseId]; if (!item) return false;
        item.output = (item.output + String(raw == null ? '' : raw)).slice(-maxOutputBytes);
        const start = item.output.lastIndexOf('__WORKSPACE_CAPTURE_RESULT_B64__');
        const end = item.output.indexOf('__WORKSPACE_CAPTURE_END__', start);
        if (start < 0 || end < 0) return false;
        const encoded = item.output.substring(start + '__WORKSPACE_CAPTURE_RESULT_B64__'.length, end).replace(/\s/g, '');
        const session = workspaceModule.sessions.get(item.sessionId); if (!session) return true;
        try {
            const result = JSON.parse(Buffer.from(encoded, 'base64').toString('utf8'));
            const data = result.data || {};
            if (result.state === 'error') throw new Error(data.message || 'Capture failed.');
            const image = Buffer.from(String(data.image || ''), 'base64');
            if (image.length < 100 || image.length > 3 * 1024 * 1024) throw new Error('Invalid capture image size.');
            images.set(session.id, { buffer:image, ownerId:session.ownerId, updatedAt:Date.now() });
            session.captureState = 'ready'; session.captureError = null;
            session.captureWidth = data.width || null; session.captureHeight = data.height || null;
            session.captureWindowCount = data.windowCount == null ? null : data.windowCount;
            session.captureVersion = Date.now(); session.captureUpdatedAt = new Date().toISOString();
        } catch (error) {
            session.captureState = 'error'; session.captureError = error.message; session.captureUpdatedAt = new Date().toISOString();
        }
        delete outputs[responseId]; return true;
    }

    function captureAgentData(command, agent) {
        if (!command || command.action !== 'msg') return;
        if (command.type === 'runcommands' && typeof command.responseid === 'string' && command.responseid.indexOf('workspace-capture-') === 0) { consume(command.responseid, command.result); return; }
        if (command.type === 'console' && agent && agent.dbNodeKey && typeof command.value === 'string') { const responseId = pendingByNode[agent.dbNodeKey]; if (responseId && consume(responseId, command.value)) delete pendingByNode[agent.dbNodeKey]; }
    }

    function getImage(user, sessionId) {
        const session = getSession(user, sessionId);
        const image = images.get(session.id);
        if (!image) throw new Error('Capture image not found.');
        return image.buffer;
    }

    return { capture, getImage, captureAgentData };
};