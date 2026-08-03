# Full product audit

Repository: `Eris92/SIRK-Agent`
Commit: `5ccafa38bf9089434a93cca2070e13b58c5b6021`

## Summary

```json
{
  "files": 135,
  "textFiles": 134,
  "lines": 16199,
  "extensions": {
    ".cs": 71,
    ".csproj": 11,
    ".js": 2,
    ".json": 3,
    ".md": 14,
    ".ps1": 17,
    ".txt": 2,
    ".wxs": 1,
    ".yml": 13,
    "<none>": 1
  },
  "projects": 11,
  "nodeArtifacts": 0,
  "legacyPaths": 0,
  "findingsBySeverity": {
    "critical": 0,
    "high": 0,
    "medium": 7,
    "low": 0,
    "info": 0
  }
}
```

## Highest severity findings

- **MEDIUM** `incomplete-implementation` — `.github/workflows/management-full-ci.yml:143` — Set-Content (Join-Path $root 'quarantine-state.bin') 'test-placeholder'
- **MEDIUM** `plaintext-http-url` — `browser-extension/manifest.json:8` — "host_permissions": ["http://*/*", "https://*/*"],
- **MEDIUM** `plaintext-http-url` — `browser-extension/manifest.json:12` — "matches": ["http://*/*", "https://*/*"],
- **MEDIUM** `plaintext-http-url` — `browser-extension/service-worker.js:41` — }, { urls: ["http://*/*", "https://*/*"] });
- **MEDIUM** `plaintext-http-url` — `browser-extension/service-worker.js:47` — }, { urls: ["http://*/*", "https://*/*"] });
- **MEDIUM** `plaintext-http-url` — `installer/Product.wxs:1` — <Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
- **MEDIUM** `shell-execute-enabled` — `src/SirkAgent.Report/Program.cs:67` — try { Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true }); }
