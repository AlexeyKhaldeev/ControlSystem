# Windows Agent v0.2 — validation and operations

Target: Windows 10/11 x64. This preparation was performed on macOS; native API,
SCM, Event Log, ACL and reboot acceptance remain mandatory on a real Windows PC.
No services were installed or modified during development. No server/DTO changes.

## Audit and metric semantics

The agent opens outbound HTTPS POST /api/v1/heartbeats; no inbound listener or persistent
connection protocol. Provision a stable UUID and a unique random 256-bit token out of
band; the server stores only its SHA256. Keep UUID/token through updates and reboots;
do not clone the same identity to another PC. Version comes from the installed assembly.
Sample timestamp is UTC; server controls LastSeenAtUtc. Synchronize clocks with NTP.

CPU is delta (kernel + user - idle)/(kernel + user), percentage 0–100; kernel already
includes idle. First sample, failed/reset/invalid counters and unsupported multi-group
CPU measurement produce null, never fabricated zero. GetSystemTimes represents only a
processor group on >64 logical CPUs: such machines require another collector before
CPU acceptance. It measures CPU busy time, which can differ from Task Manager's
frequency-adjusted utilization; compare trends over matching intervals.
RAM is physical total/available bytes; used = total - available, not process working set
or commit. Mandatory RAM failure suppresses that sample and retries; DTO cannot express
missing RAM. Fixed ready disks report total/free bytes (not user-quota available bytes);
inaccessible/invalid disks are omitted, at most 32. Missing disks do not mean zero free.
Uptime is OS GetTickCount64 milliseconds converted to whole seconds, not process age.
Fast Startup/sleep may differ from an operator's idea of a cold boot; test Restart.

Retries use fresh snapshots; there is no offline queue, overlapping send or backlog.
Healthy cadence is 10 seconds; failed attempts wait bounded 10/20/40/60 seconds plus
<2 seconds jitter. HTTP timeout is 5 seconds. Stop cancels requests and delay. Fatal
unexpected worker errors terminate with failure for SCM recovery; transient network,
CPU/disk and native-read errors do not require process restart. Synchronous OS calls
cannot be forcibly cancelled while inside Windows; a stalled storage driver remains
an acceptance risk. Response bodies are not buffered by the heartbeat client.

## Publish

From repository root with .NET 10 SDK (macOS cross-publish supported):

```text
dotnet restore ControlSystem.sln
dotnet build ControlSystem.sln
dotnet test ControlSystem.sln
dotnet publish src/ControlSystem.Agent/ControlSystem.Agent.csproj -c Release -r win-x64 --self-contained true -p:EnableWindowsService=true -o artifacts/windows-agent
```

Or run `scripts/windows/publish-agent.ps1` in PowerShell. Copy the **entire** publish
folder via an approved channel, verify its provenance/hash before executing. No Runtime
installation is needed for self-contained output. Do not trim or use --simulate in SCM.

## Install (explicit operator action, elevated Windows PowerShell 5.1)

Use a local staging folder with restricted ACL. Install binaries under
`C:\Program Files\ControlSystem.Agent`. Stop if that directory/service already exists;
follow update instructions instead. Do not accept binaries or configuration writable by
ordinary users. The examples below change services **only when an operator runs them**.

1. Copy the publish folder. Provision `appsettings.Production.json` in the installed
   folder using a secure editor; do not put token in command line/history, source or
   service ImagePath. Example structure (placeholders must be replaced privately):

```json
{
  "Agent": {
    "ServerUrl": "https://monitor.example.lan:7443/",
    "DeviceId": "REPLACE-WITH-PROVISIONED-UUID",
    "Token": "REPLACE-WITH-UNIQUE-RANDOM-256-BIT-TOKEN",
    "TrustedCaPath": "C:\\Program Files\\ControlSystem.Agent\\monitor-root-ca.pem"
  },
  "Logging": {
    "LogLevel": { "Default": "Information" },
    "EventLog": { "LogLevel": { "Default": "Information" } }
  }
}
```

2. The server certificate must match its DNS name and be current. Use an absolute
   TrustedCaPath for a trusted public root PEM, or omit it for Windows system trust.
   Custom root mode checks hostname/chain/serverAuth/expiry but has no CRL/OCSP and no
   AIA download; server must send intermediates. Never install a private key on agents.
3. Protect the whole installation, including token config and public CA against changes.
   On a fresh directory with no custom grants, remove inherited access and grant only
   Administrators/System full control and LocalService read/execute:

```powershell
$dir = 'C:\Program Files\ControlSystem.Agent'
icacls.exe $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX' /T
if ($LASTEXITCODE -ne 0) { throw 'ACL configuration failed' }
icacls.exe $dir
```

   Inspect for pre-existing explicit Users/Everyone write/read grants and remove them
   before proceeding. LocalService is shared by services; this is not isolation from
   other LocalService processes. Use a dedicated managed account if that threat matters.
   Do not make the binary directory writable to the service account.
4. Register an Event Log source once as administrator and create service under
   LocalService, not LocalSystem. PowerShell below uses argument arrays for sc.exe:

```powershell
if (-not [System.Diagnostics.EventLog]::SourceExists('ControlSystem.Agent')) {
    New-EventLog -LogName Application -Source 'ControlSystem.Agent'
}
$bin = '"C:\Program Files\ControlSystem.Agent\ControlSystem.Agent.exe" --environment Production'
New-Service -Name 'ControlSystem.Agent' -BinaryPathName $bin -DisplayName 'ControlSystem Agent' -StartupType Automatic -ErrorAction Stop | Out-Null
# New-Service does not start the service. Set its account before the first start.
sc.exe config ControlSystem.Agent obj= 'NT AUTHORITY\LocalService'
if ($LASTEXITCODE -ne 0) { throw 'Service account configuration failed' }
$svc = Get-CimInstance Win32_Service -Filter "Name='ControlSystem.Agent'"
if ($svc.PathName -cne $bin -or $svc.StartName -ne 'NT AUTHORITY\LocalService') {
    throw 'Unexpected service ImagePath or account; do not start'
}
sc.exe failure ControlSystem.Agent reset= 86400 actions= restart/60000/restart/60000/restart/60000
if ($LASTEXITCODE -ne 0) { throw 'Recovery configuration failed' }
sc.exe failureflag ControlSystem.Agent 1
if ($LASTEXITCODE -ne 0) { throw 'Failure flag configuration failed' }
sc.exe qc ControlSystem.Agent
sc.exe qfailure ControlSystem.Agent
Start-Service ControlSystem.Agent
Get-Service ControlSystem.Agent
```

5. Inspect Application Event Log (source ControlSystem.Agent), server last-seen and
   identity. Confirm process command line has no token. Do not rely solely on RUNNING.
   Console logs are available for a controlled foreground run; Event Log information
   level requires the explicit EventLog filter above. No unbounded local file logger.

## Stop, update, remove

Stop with `Stop-Service ControlSystem.Agent`; wait for `Stopped` and process exit
before replacing files. Normal SCM stop must not restart the service. If it fails to
stop, diagnose blocked I/O; do not replace an in-use installation.

For update: back up the existing complete binary folder and protected config securely;
stop and wait; replace the complete published binaries while preserving UUID/token/CA
and production config; reapply and verify ACL; start; confirm heartbeat and new version.
Do not overwrite protected config with a build artifact. On failure, stop and restore
the previous complete release/config, verify ACL and start. No automatic updater exists.

For removal: stop and wait, then `sc.exe delete ControlSystem.Agent` (check exit code).
If marked for deletion, close Services consoles and retry inspection later. Revoke the
agent token in server provisioning; delete protected local secrets/backups according to
operator policy. Keep diagnostic records as required. Service deletion does not erase
files or revoke credentials. Do not delete the shared Application log.

## Manual acceptance on Windows 10 AND Windows 11

Record OS build, hardware/logical CPU count, agent version/hash, time zone/NTP, server
version, dates, screenshots/counter values and pass/fail for each step. Use a test PC
and dedicated server identity; planned fault/reboot tests require operator approval.

1. Publish/install as above. Confirm LocalService, quoted ImagePath, auto start,
   recovery delays, no inbound port, correct UUID/version and isSimulated=false.
2. First CPU sample null; after >=10 seconds compare several idle/busy samples to
   Task Manager and perf counters over equal windows. Exercise all-core workload;
   validate 0–100 and return to idle. On >64 logical CPUs expect unavailable CPU.
3. Compare total/available physical RAM and derived used bytes with Task Manager /
   Get-CimInstance Win32_OperatingSystem (KiB there: multiply by 1024). Allocate/release
   memory safely and observe trends. Compare fixed disk bytes with Get-Volume; test
   inaccessible/offline secondary volume. Verify boot volume is present, no fake zeros.
4. Compare uptime before/after service restart (must continue) and OS Restart (resets).
   Reboot and verify automatic service start and same identity/token.
5. Disconnect network for >=5 minutes; service stays alive, bounded retries and stable
   memory, dashboard Offline after >35 seconds. Restore network: fresh heartbeat within
   maximum backoff + request time, no burst/backlog. Repeat with server stopped and
   restarted, DNS failure and blackhole TCP/TLS endpoint (timeout near 5 seconds).
6. In a test environment exercise 401/403/429/500 and recovery to 200. Verify logs
   contain status/type but no token, headers, body or private config. Confirm no redirect
   following. Test untrusted CA, wrong hostname, expired cert: all must reject; correct
   cert restores connection without insecure overrides.
7. Stop during healthy send, blackhole request and backoff; time SCM stop, ensure no
   further sends/process and no recovery on intentional stop. Restart successfully.
8. With explicit approval terminate the test service process abnormally; check SCM
   recovery after ~60 seconds and new heartbeat. Separately verify unexpected worker
   fault produces nonzero process exit on Windows (unit fake is not SCM evidence).
9. Run >=24-hour network fault/reconnect soak; sample working set/private bytes,
   handle/thread/socket count, event-log volume and server request timing. No unbounded
   trend, no concurrent heartbeat, max failed retry frequency as above. Event Log is
   bounded by Windows retention policy; set appropriate host policy operationally.
10. Perform update/rollback/removal exercise. Confirm identity preserved on update and
    token revoked on removal. Retain acceptance evidence; Windows validation is not
    complete until all applicable cases pass.

## Sources

- [Microsoft Windows Worker Service / recovery / Event Log](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service)
- [GetSystemTimes CPU semantics and processor groups](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getsystemtimes)

## Validation report — 27 November 2026

Worktree `windows-agent-validation`, branch `codex/windows-agent-validation`, based on
local `main`. macOS arm64, SDK 10.0.100. No commit, push, merge, deployment or service
changes were performed. Windows acceptance remains open.

| Audit defect | Resolution |
|---|---|
| CPU API failure dropped RAM/disks/uptime | Nullable CPU and reset baseline; injectable real native source |
| >64 logical CPUs reported group as system | CPU unavailable with warning; explicit hardware limitation |
| RAM/uptime/disk invalid values | Validate mandatory metrics; skip invalid disks; validate payload before send |
| Version always 0.1.0 | Assembly informational version, package version 0.2.0 |
| Null/whitespace/header-invalid token | Startup validation rejects these inputs |
| Large response buffered in memory | ResponseHeadersRead, dispose response without consuming body |
| Long failed operation erased backoff | Full bounded delay after each failed attempt |
| Fatal worker clean StopHost prevented recovery | Production Environment.Exit(1), injectable fake for tests |
| Service logging/install pitfalls | Explicit EventLog source, LocalService, ACL, managed quoted ImagePath |

Verification outside sandbox (sandbox cannot write the newly created worktree or
perform all restore/network/keychain operations):

- `dotnet restore ControlSystem.sln`: exit 0.
- Final `dotnet build ControlSystem.sln`: exit 0, 0 warnings, 0 errors (2.40 s).
- Final `dotnet test ControlSystem.sln`: exit 0, 56 passed, 0 failed, 0 skipped (4 s).
  Includes 19 new Agent cases with fake Windows APIs/disks, fake clock/HTTP, cancellation,
  fatal termination, retry/reset, memory/disk/CPU/uptime edges. Existing 37 remain passing.
- Final self-contained Release win-x64 cross-publish: exit 0; executable is PE32+ x86-64; includes
  runtime, WindowsServices, EventLog and ServiceController dependencies. Executing the
  artifact on Windows is not validated by this result.
- Foreground `--simulate` smoke with test-only identity and unavailable loopback server:
  host/DI started, network failure logged without credentials, SIGINT stopped cleanly, exit 0.
- `git diff --check`: exit 0.
- Initial build found xUnit cancellation analyzer errors and cross-publish found an
  EventLog platform guard error; both corrected before the successful final checks.

Independent reviewer report: read-only review, no blocking findings after corrections.
Reviewer independently identified response buffering, fatal recovery, CPU availability,
processor groups, version and retry issues, and checked final DI/cancellation semantics,
publish service assets, and installation quoting/account checks. Reviewer did not run
build/test. Additional tests of RAM failure, invalid uptime, slow failure and HTTP
cancellation reviewed as meaningful. Remaining acceptance: native API accuracy and
hardware behavior, SCM stop/recovery, ACL, EventLog, reboot and long-running soak.

Changed files (all within authorized scope):

- `src/ControlSystem.Agent/AgentOptions.cs`
- `src/ControlSystem.Agent/ControlSystem.Agent.csproj`
- `src/ControlSystem.Agent/Program.cs`
- `src/ControlSystem.Agent/Telemetry/WindowsTelemetryCollector.cs`
- `src/ControlSystem.Agent/Telemetry/WindowsTelemetrySource.cs` (new)
- `src/ControlSystem.Agent/TelemetryWorker.cs`
- `src/ControlSystem.Agent/Transport/HeartbeatClient.cs`
- `tests/ControlSystem.Tests/WindowsAgentTests.cs` (new)
- `scripts/windows/publish-agent.ps1` (new)
- `docs/windows-agent.md` (new)
- Windows Agent section of `docs/tasks.md`
