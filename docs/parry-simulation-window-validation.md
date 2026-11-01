# Parry simulation window validation

## Change and limits

Parry input counted simulation ticks, while native damage hooks compared a UTC
deadline. A stalled game loop could therefore reject a hit while the input
window still had ticks remaining. The press update also consumed the first
tick before native impacts ran, leaving a one-tick window with no impact sample.

Native gates now compare the simulation tick to the per-slot deadline captured
on the fresh press. The press tick is included: a window of N ticks accepts
impacts on ticks F through F+N-1 and rejects them at F+N. Deferred VRR render
updates do not advance this clock. An already resolved window retains its
original deadline even though its input-state countdown stops. Runtime reset,
window close and cue clear revoke the per-slot deadlines and armed attacker IDs.

Authored difficulty durations, press semantics, whiff recovery and delayed
commit markers are unchanged. The fix does not establish per-hit identity:
the existing action-wide resolution masks can still protect later hits of the
same action. Physical, magic, status and lethal interception still need native
playtest evidence; a successful build is not such evidence.

## Automated evidence

The production arm, native-window predicate, revoke and countdown methods are
linked into the existing partial-module fixture test project. Tests do not load
FFX or execute native trampolines. The VRR test uses the production ownership
binding and dispatch methods with the timing fixture.

| Reproduction | Before | After |
| --- | --- | --- |
| Pause wall time for 100 ms without advancing a one-tick simulation window | Native window expired | Native window remains valid |
| Count native samples for 1, 6 and 12 ticks, including the press tick | One sample lost | Exactly N samples |
| Resolve, then advance beyond the original deadline without waiting in wall time | Native window remains valid | Native window expires |
| Reset while a deadline is live, then arm a different target | Stale slot remains armed | Old deadline revoked |
| Ten deferred render updates followed by two owned simulation ticks | No prior regression test | Only owned ticks consume the two-tick window |
| Whiff, attempt input throughout three recovery ticks, press again | Existing recovery behavior | Rejection during recovery, fresh press accepted afterward |

The timing regressions were observed failing before the fix: five failures in
the first six cases, plus a separate failing reset case. The final Debug runs
passed all 34 build tests and all 162 Parry tests, with no skipped tests. The
Release win-x86 mod compiled with zero warnings and zero errors against the
pinned Fahrenheit 1.0.0-alpha12 SDK/core packages.

Normal repository validation uses:

```powershell
.\build.cmd mod-verify --sdk local --configuration Release
```

Canonical Release distribution validation subsequently passed: 158 Release
Parry tests and 34 build/integration tests, no failures or skips, and a validated
win-x86 payload. The pinned Fahrenheit core rebuild emitted three existing
upstream warnings; the Parry build emitted none. Debug-only cases account for
the difference from the 162-test Debug result above.

The ownership failure came from running local Git transport as a different
Windows sandbox account than the repository owner. A local clone of the existing
pinned checkout, followed by the canonical command under the repository owner's
account, succeeded. No persistent Git trust configuration or wildcard exception
was added. A Git ownership error is not evidence of a source or ABI problem.

Earlier focused verification used the existing alpha12 feed:

```powershell
dotnet restore Fahrenheit.Mods.Parry.csproj --source <local-alpha12-feed> --source https://api.nuget.org/v3/index.json
dotnet restore tests/Parry.Tests/Parry.Tests.csproj --source <local-alpha12-feed> --source https://api.nuget.org/v3/index.json
dotnet test build/tests/ModBuild.Tests.csproj --nologo --verbosity minimal
dotnet test tests/Parry.Tests/Parry.Tests.csproj --no-restore --nologo --verbosity minimal
dotnet build Fahrenheit.Mods.Parry.csproj -c Release --no-restore --nologo --verbosity minimal
git diff --check
```

## VRR integration boundaries

The inspected VRR source snapshot is based on commit `34ccaae` plus recorded
working-tree changes. Ordinary corrected60 operation keeps Parry on stock
PreUpdate. Cooperative dispatch belongs to the explicit independent-presentation
path: ordinary clock completion and the qualified script-input snapshot precede
Parry dispatch. Extra presentation passes dispatch no Parry update. Root, thread,
image, invocation, epoch and snapshot ownership checks reject stale or duplicate
dispatches. These are source contracts, not native combat results.

The current source does not admit production combat with independent 120/144-Hz
presentation. Its native-field probe excludes combat and Parry. Do not use that
probe as a compatibility test or enable it to bypass the qualification boundary.

The installed VRR DLL inspected during preparation predates the cooperation and
input-probe contracts. Its SHA-256 is
`b17ffc6b8829e987b404153ce5db87486fbf96fb6b699ae3f96bf97ea36da6df`.
It does not match the current source payloads. A test against that installation
cannot qualify the current source integration.

Both mods subsequently passed their canonical Release verification against the
official alpha12 SDK: 158 Parry tests plus 34 build/integration tests, and 2,484
VRR tests. VRR was built from a separate hash-checked source snapshot, retaining
the active VRR working tree. The new payloads were not installed together.

### Direct source integration test

```powershell
.\build.cmd verify-vrr --vrr-source-dir <reviewed-vrr-source-root>
```

This explicitly selected source root must contain the four production files
`FixedSimulationClock.cs`, `VrrModule.CooperativeTicks.cs`, `RenderPassContext.cs`
and `PresentationAdmission.cs` under `src/`. The test links those files with
Parry's production simulation binding and window timing; it does not copy an
implementation into a mock. Native admission, clock and snapshot boundaries are
synthetic host inputs and do not qualify native ordering or independent images.

All eight cases passed through the canonical workflow. The rate matrix covers
30/60-Hz simulation with 120/144-Hz image schedules. Other cases cover zero-step
images, speed-zero pause, catch-up, stale/duplicate identities, epoch advance and
native handback. A deliberate control that dispatched presentation-only images
as simulation failed all four matrix cases; restoring the host passed all eight.
Missing or incomplete source roots fail explicitly. No physical-input or held-edge
behavior is inferred from this fixture.

Each successful run retains TRX results and `provenance.json` under
`.workspace/vrr-integration/<run>/`. Provenance hashes all 13 linked source,
fixture and project inputs plus the workflow/build sources and present SDK/feed
configuration files. It checks that inputs stayed unchanged during the run,
records the effective SDK version, Release configuration, result hash and test
counters, and marks native validation false.

The first startup-only session was deferred before combat. Subsequent recorded
native runs used the `16c32fd` diagnostic payload: one successful physical parry
without VRR and four with ordinary corrected60. Each correlated delayed commit
remained blocked. Whiffs closed after six versus twelve simulation ticks,
approximately 200 ms in both modes. The later `1408025` correction changes only
internal argument metadata, not gameplay; that exact payload was not replayed.

The corrected60 run exposed alternating effect visibility. A separate isolated
VRR regression (`1fa86f7`) reproduces the mixed step/draw omission: four ordinary
ticks yield two draw callbacks without the held-draw backend. That regression
remains red and the render defect unfixed. These observations do not qualify
ordinary corrected60 as a complete presentation mode.

Both games were stopped and original mod files, settings and loadorder restored;
all 479 pre-test input hashes match. Detailed immutable capture hashes and case
ledgers are in the KB reports `forensics/parry-native-baseline-20261101.md`,
`forensics/parry-vrr60-native-20261101.md` and
`forensics/vrr-fixed60-effect-draw-loss-20261101.md`.

## Native validation still required

For an explicit diagnostic process, set `FFX_PARRY_NATIVE_VALIDATION=1` before
launch and enable Parry logging. Both are required; ordinary startup leaves the
trace off. No additional native hook is installed. Records are appended to the
existing session debug log with the `[native-validation]` prefix followed by
JSON. Clear the process environment variable after the run.

The capture is bounded to 12,000 records per module instance. Sequence 12,000
means the limit was reached; a callback can then lack its post record. Treat
missing pairs and `incomplete` outcomes as incomplete evidence. `read_ok=false`
means actor fields are unavailable, not native zero values. Diagnostic exceptions
are isolated from gameplay and original calls are never retried by the recorder.

Each callback records pre/post sequence, QPC/frequency, managed thread, simulation
tick, callback/parent IDs, actual original-call count and blocked/passed outcome.
The actor snapshot uses read-only `ReadProcessMemory` against the existing typed
Chr layout, recording HP, staged damage, will-die/death, status and both bank
headers before any hook suppression. Lifecycle records cover fresh presses,
rejections, open/end/reset and cue clear. They report `observed`, not a damage
decision. Slot -1 identifies an input with no current party target.

`window_generation` identifies an observed press window, not a native hit or
action. Its stored start/deadline survive gate clearing for delayed-commit
correlation; `gate_deadline_tick` reports the actual current gate. Cue index,
first-seen tick, signature and masks are separate context. Calc `command_id`,
commit `command_index` and motion `motion_mode` are different identity domains;
unused fields are -1. Motion has no native attacker argument, so `attacker=-1`
and `cue_attacker` supplies explicitly contextual identity. `batch_hit_count`
is outer calc p11, never a hit ordinal. Internal calc argument 11 is a pointer,
so its batch count is unknown (-1) and the ordinary debug line labels the raw
argument separately. Earlier `16c32fd` captures incorrectly copied this pointer
into internal `batch_hit_count`; ignore that field on internal calc records.
Both independent cursor/count/attacker/index
bank headers are observations only: one callback can span both banks and no
per-hit identity or multi-hit correctness is inferred from them.

Enable existing session logging only for the diagnostic run. Capture the fresh
press, simulation frame, native hook sequence, HP/status/death state and final
outcome. Repeat with standalone updating and qualified VRR simulation ownership.

| Case | Required observation |
| --- | --- |
| Physical single hit | Fresh R1 accepts an impact before the deadline; a hit at the deadline applies normally |
| Magic/status hit | HP and status commit are blocked for the accepted hit |
| Lethal hit | Successful interception precedes HP subtraction and death latching |
| Delayed finalization | The correlated p5=1024 commit stays blocked after the active window expires |
| Multi-hit | Distinguish each hit from duplicate finalization; record inherited protection as an unresolved defect |
| Stalled update | Wall delay alone does not close the window; advancing N simulation ticks does |
| Disable/reset/re-enable | No old slot deadline accepts damage without another fresh press |
| VRR | Deferred presentation updates do not consume window or recovery samples |

The native evidence covers bounded physical cases, including one potentially
lethal staged hit. Successful magic, sequential multi-hit defense, the complete
pause/reset matrix and independent 120/144-Hz presentation remain unqualified.
The VRR clock/input JSON budget expired before the first recorded parry, so the
ordinary-clock/input-publication join still requires a targeted capture with
sufficient coverage. Visual contact timing and map frame pacing were not measured.
Automated checks alone establish no native result or E33 timing measurement.

## Optional QoL source integration

```powershell
.\build.cmd verify-vrr --vrr-source-dir <VRR-checkout> --qol-source-dir <QoL-checkout>
```

The optional QoL source adds four cases to the existing eight clock/dispatch
cases. It links the production focus policy and existing QoL fixture, then
delivers the published button level through the production Parry/VRR cooperation
code. Cases cover standalone and cooperative 30/60-Hz updates, repeated focus
cycles, publication without a fresh input transformation, catch-up, epoch reset,
120/144-Hz extra presentations and genuine release/repress. Native pad-to-script
translation and the Parry input body remain fixture boundaries. Device acquisition
validity is tested separately in QoL; this suite does not emulate native polling.

The workflow hashes the selected QoL inputs alongside its existing inputs and
rejects source changes during a run. Removing the QoL blocked-button mask in an
isolated negative-control source makes all four focus cases fail. Omitting
`--qol-source-dir` retains the existing eight-case two-mod suite.
