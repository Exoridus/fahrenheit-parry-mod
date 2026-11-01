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

Canonical distribution validation was not completed. The isolated checkout
lacked its own Fahrenheit checkout, and a preparatory local clone was rejected
by Git's repository ownership check in the sandbox. Verification instead used
the existing alpha12 feed without changing tracked NuGet configuration:

```powershell
dotnet restore Fahrenheit.Mods.Parry.csproj --source <local-alpha12-feed> --source https://api.nuget.org/v3/index.json
dotnet restore tests/Parry.Tests/Parry.Tests.csproj --source <local-alpha12-feed> --source https://api.nuget.org/v3/index.json
dotnet test build/tests/ModBuild.Tests.csproj --nologo --verbosity minimal
dotnet test tests/Parry.Tests/Parry.Tests.csproj --no-restore --nologo --verbosity minimal
dotnet build Fahrenheit.Mods.Parry.csproj -c Release --no-restore --nologo --verbosity minimal
git diff --check
```

## Native validation still required

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

No in-game run, damage prevention result or E33 timing measurement is claimed
by these automated checks.
