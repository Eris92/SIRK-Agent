'use strict';

module.exports = function workspacePlugin(parent) {
    const obj = {};
    obj.parent = parent;
    obj.exports = ['onWebUIStartupEnd', 'onDeviceRefreshEnd', 'goPageStart', 'goPageEnd'];

    obj.server_startup = function serverStartup() {
        parent.workspaceSessions = parent.workspaceSessions || new Map();
        console.log('[MeshCentral-Workspace] Plugin 0.1.0 loaded');
    };

    obj.onWebUIStartupEnd = function onWebUIStartupEnd() {
        return `
<script>
(function () {
    if (window.MeshCentralWorkspaceBootstrap) return;
    window.MeshCentralWorkspaceBootstrap = true;
    var script = document.createElement('script');
    script.src = '/pluginadmin.ashx?pin=MeshCentral-Workspace&user=1&file=main.js';
    script.defer = true;
    document.head.appendChild(script);
    var style = document.createElement('link');
    style.rel = 'stylesheet';
    style.href = '/pluginadmin.ashx?pin=MeshCentral-Workspace&user=1&file=main.css';
    document.head.appendChild(style);
})();
</script>`;
    };

    obj.onDeviceRefreshEnd = function onDeviceRefreshEnd() {
        return '<script>window.MeshCentralWorkspace && window.MeshCentralWorkspace.refresh();</script>';
    };

    obj.goPageStart = function goPageStart() { return ''; };
    obj.goPageEnd = function goPageEnd() { return ''; };

    return obj;
};
