# Architecture

## Components

1. **MeshCentral-Plugin** adds the `Pulpit -New` device tab and owns server-side workspace session state.
2. **WorkspaceHost** runs on the Windows endpoint in the interactive user session.
3. **WorkspaceCommon** contains protocol records shared by endpoint components.

## Version 0.1 flow

```text
Browser -> plugin session API -> session record
WorkspaceHost -> named pipe heartbeat -> future MeshAgent bridge
```

The browser UI and host heartbeat are implemented. The remaining integration boundary is the MeshAgent transport that starts `WorkspaceHost.exe` and forwards named-pipe messages to the plugin.

## Security rules

- The original MeshCentral Desktop module is not modified.
- Every operation must be scoped to an authenticated MeshCentral user and a permitted node.
- Session tokens are random and must be validated before accepting heartbeat data.
- WorkspaceHost must run in the selected interactive Windows session, not silently as SYSTEM.
- Logs must not contain passwords, API keys or clipboard contents.
