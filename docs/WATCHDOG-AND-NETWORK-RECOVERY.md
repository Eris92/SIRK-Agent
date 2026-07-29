# SIRK Agent Watchdog and network recovery

`SirkAgentWatchdog` is a separate, delayed-auto Windows service. It intentionally
does not load policy, browser, remote desktop, activity or reporting modules.
Its only protected target is the main `SirkAgent` service.

## Health gates

The watchdog samples every five seconds:

- SCM state and presence of the service process;
- age of `heartbeat-latest.json`;
- normalized process CPU;
- private memory.

Recovery requires:

- immediate action when SCM is not running or its process is absent;
- three consecutive stale-heartbeat or memory samples;
- six consecutive CPU samples above 90%.

The initial private-memory recovery limit is 1 GiB. At most three automatic
restarts are permitted in a rolling 15-minute window. Further restarts are
suppressed and recorded to prevent a recovery loop.

State and incidents are stored under:

```text
C:\ProgramData\SIRK\Agent\Watchdog\watchdog-status.json
C:\ProgramData\SIRK\Agent\Watchdog\watchdog-incidents.jsonl
```

The main Agent includes watchdog state in authenticated Portal check-ins.

## Network changes

The main service subscribes to `NetworkAddressChanged` and
`NetworkAvailabilityChanged`. Events are debounced for 250 ms, the current
interface/address snapshot is atomically written to `network-status.json`, the
active long-poll is cancelled and a new authenticated check-in starts
immediately. The Portal records both this snapshot and the connection source
address forwarded by the TLS gateway.

## Update ownership

The watchdog is the intended owner of the final signed-update transaction:
stop main service, verify/stage, replace main runtime, start, evaluate the
health gate and roll back. This transaction is not considered complete until
the watchdog executable can be updated independently without replacing a
running file and its failure/rollback tests pass.
