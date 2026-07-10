# Kampf-Labels, Dodge-Bugfix und Camera-Lock-Isolation — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Den Dodge-Marker korrekt räumen, die Kampf-Labels an den Anker der Engine-Schadenszahlen hängen, sie nach Timing-Präzision einfärben, und die Magie-Kamera-Unterdrückung separat schaltbar machen.

**Architecture:** Drei neue reine, statische Helfer (`DodgeCommitGate`, `OverlayAnchorMath`, `CombatLabelPalette`) tragen die Entscheidungslogik und werden per xUnit getestet. `ParryModule.*` bleibt der Ort für Seiteneffekte (Hooks, Speicherzugriff, ImGui). Das folgt dem bestehenden Muster von `ParryInputStateTransitions`, das genau deshalb existiert: `ParryModule` ist über `FhMethodHandle`-Hooks im Konstruktor FFX-gekoppelt und nicht instanziierbar im Test.

**Tech Stack:** C# / .NET 10, `net10.0-win-x86`, Fahrenheit alpha09, xUnit 2.9.3, NUKE-Build über `.\build.cmd`.

## Global Constraints

- **Conventional Commits sind Pflicht.** Ein `commit-msg`-Hook (`core.hooksPath = .githooks`) prüft jede Nachricht und lehnt Verstöße ab.
- **Mehrzeilige Commit-Nachrichten nur über das PowerShell-Tool** mit Here-String (`@'` … `'@`, schließendes `'@` in Spalte 0). Das Bash-Tool kann diese Syntax nicht und zerlegt die Nachricht.
- **Validierung:** `.\build.cmd verify` (Build + Tests). Immer Windows-Syntax in Doku und Befehlen.
- **Neue reine Helfer müssen in `tests/Parry.Tests/Parry.Tests.csproj` als `<Compile Include=... Link=... />` eingetragen werden**, sonst sieht das Testprojekt sie nicht. Das Testprojekt referenziert das Mod-Projekt *nicht*, es pickt einzelne Quelldateien.
- Reine Helferdateien dürfen **keine** globalen Usings aus `src/Usings.cs` voraussetzen (die Datei wird im Testprojekt nicht kompiliert). Nötige `using`-Direktiven explizit in die Datei schreiben.
- Stil: file-scoped namespaces, `PascalCase` für Typen/Methoden, `_camelCase` für private Felder, `camelCase` für Locals/Parameter, ein Haupttyp pro Datei.
- `lang/de-DE.json` verwendet durchgängig ASCII-Ersatzschreibungen (`Zuege`, `haelt`) statt Umlaute — vermutlich wegen fehlender Glyphen im ImGui-Font. Neue Einträge folgen dieser bestehenden Konvention, damit die Datei konsistent bleibt.
- Nach jeder Änderung: eingeführte Warnungen auflösen, toten Code entfernen.

**Spec:** `docs/superpowers/specs/2026-07-10-labels-camera-overdrive-design.md` (Commit `fa59bc6`).

## File Structure

| Datei | Verantwortung | Aktion |
|---|---|---|
| `src/combat/DodgeCommitGate.cs` | Reine Entscheidung, ob ein `MsSetDamageInternal`-Commit wegen eines Dodge übersprungen wird | Neu |
| `src/overlay/OverlayAnchorMath.cs` | Reine Anker-Mathematik: Behind-Camera-Prädikat, Viewport-Test, Virtual→Screen | Neu |
| `src/overlay/CombatLabelPalette.cs` | Füllfarbe eines Kampf-Labels nach Timing-Präzision | Neu |
| `src/ParryModule.Combat.cs` | `clear_awaiting_turn_end` räumt den Dodge-Marker | Ändern |
| `src/ParryModule.Hooks.cs` | `p5=1024`-Skip nutzt `DodgeCommitGate`; Magie-Kamera-Gate | Ändern |
| `src/ParryModule.Overlay.cs` | Anker liest `0xf44/0xf48`; Farbverzweigung; toter VU0-Pfad raus | Ändern |
| `src/ParryModule.cs` | Neues Feld `_optionMagicCameraLock`; Setting registrieren | Ändern |
| `src/ParryModule.Config.cs` | Persistenz für `MagicCameraLock` | Ändern |
| `src/ParryModule.Settings.cs` | Renderer für das neue Setting | Ändern |
| `lang/en-US.json`, `lang/de-DE.json` | Name + Beschreibung des neuen Settings | Ändern |
| `tests/Parry.Tests/*` | Unit-Tests der drei Helfer | Neu |

`src/overlay/` existiert noch nicht und wird angelegt — parallel zu den bestehenden `src/combat/` und `src/debug/`.

---

### Task 1: Dodge-Marker räumen und den Commit-Skip an den Angreifer binden

Der Korrektheitsfehler. Zuerst, weil jede Verhaltensmessung der folgenden Tasks von ihm verfälscht wird.

**Files:**
- Create: `src/combat/DodgeCommitGate.cs`
- Create: `tests/Parry.Tests/DodgeCommitGateTests.cs`
- Modify: `tests/Parry.Tests/Parry.Tests.csproj`
- Modify: `src/ParryModule.Combat.cs:813`
- Modify: `src/ParryModule.Hooks.cs:1157-1168`

**Interfaces:**
- Produces: `public static bool DodgeCommitGate.ShouldSkipCommit(bool dodgeEnabled, bool markerSet, byte armedAttackerId, byte commitAttackerId)`
- Consumes: nichts.

- [ ] **Step 1: Write the failing test**

Create `tests/Parry.Tests/DodgeCommitGateTests.cs`:

```csharp
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for <see cref="DodgeCommitGate"/>. The gate decides whether the
///     authoritative p5=1024 HP/death commit in <c>MsSetDamageInternal</c> is skipped
///     because the slot evaded. It is pure so it can be exercised without
///     <c>ParryModule</c>, which is FFX-coupled.
/// </summary>
public sealed class DodgeCommitGateTests
{
    [Fact]
    public void ShouldSkipCommit_MarkerSetAndAttackerMatches_Skips()
    {
        Assert.True(DodgeCommitGate.ShouldSkipCommit(
            dodgeEnabled: true, markerSet: true, armedAttackerId: 22, commitAttackerId: 22));
    }

    [Fact]
    public void ShouldSkipCommit_DifferentAttacker_DoesNotSkip()
    {
        // A stale marker must never swallow a different attacker's commit.
        Assert.False(DodgeCommitGate.ShouldSkipCommit(
            dodgeEnabled: true, markerSet: true, armedAttackerId: 22, commitAttackerId: 23));
    }

    [Fact]
    public void ShouldSkipCommit_NoMarker_DoesNotSkip()
    {
        Assert.False(DodgeCommitGate.ShouldSkipCommit(
            dodgeEnabled: true, markerSet: false, armedAttackerId: 22, commitAttackerId: 22));
    }

    [Fact]
    public void ShouldSkipCommit_DodgeDisabled_DoesNotSkip()
    {
        Assert.False(DodgeCommitGate.ShouldSkipCommit(
            dodgeEnabled: false, markerSet: true, armedAttackerId: 22, commitAttackerId: 22));
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `.\build.cmd verify`
Expected: FAIL — `error CS0103: The name 'DodgeCommitGate' does not exist in the current context`.

- [ ] **Step 3: Create the pure gate**

Create `src/combat/DodgeCommitGate.cs`:

```csharp
namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Pure gate for the dodge-driven commit skip in <c>MsSetDamageInternal</c>.
///
///     A slot that evaded carries a durable marker for the rest of the cue, so that a
///     multi-hit or AoE swing from the SAME attacker stays fully evaded across its
///     later hits. The marker must never outlive the cue, and it must never apply to a
///     different attacker — otherwise the character becomes immune.
///
///     Kept pure and free of side effects so it can be unit-tested without
///     <c>ParryModule</c>, which is FFX-coupled through its hook installs.
/// </summary>
public static class DodgeCommitGate
{
    /// <param name="dodgeEnabled">The <c>dodgeEnabled</c> setting.</param>
    /// <param name="markerSet">The slot's bit in the durable evade marker.</param>
    /// <param name="armedAttackerId">Attacker the dodge window was armed against.</param>
    /// <param name="commitAttackerId">Attacker driving the commit under inspection.</param>
    public static bool ShouldSkipCommit(
        bool dodgeEnabled,
        bool markerSet,
        byte armedAttackerId,
        byte commitAttackerId)
        => dodgeEnabled
        && markerSet
        && armedAttackerId == commitAttackerId;
}
```

- [ ] **Step 4: Register the new file with the test project**

Modify `tests/Parry.Tests/Parry.Tests.csproj` — add inside the existing `<ItemGroup>` that holds the `<Compile Include>` entries, keeping alphabetical order within `combat\`:

```xml
    <Compile Include="..\..\src\combat\DodgeCommitGate.cs" Link="combat\DodgeCommitGate.cs" />
```

Place it immediately before the `ParryDifficultyModel.cs` line.

- [ ] **Step 5: Run the tests and verify they pass**

Run: `.\build.cmd verify`
Expected: PASS — 4 new tests green, no build warnings introduced.

- [ ] **Step 6: Clear the dodge marker at cue end**

Modify `src/ParryModule.Combat.cs`. Locate this line inside `clear_awaiting_turn_end` (currently line 813):

```csharp
        _parryResolvedAtImpactMask = 0;
```

Replace with:

```csharp
        _parryResolvedAtImpactMask = 0;
        _dodgeResolvedAtImpactMask = 0;
```

- [ ] **Step 7: Bind the p5=1024 skip to the armed attacker**

Modify `src/ParryModule.Hooks.cs`. Replace the block currently at lines 1157-1168:

```csharp
            // Dodge finalization: skip the p5=1024 commit for a dodge-resolved slot WITHOUT
            // setting LastParriedTargetMask (that would draw a second "PARRIED" text over "DODGE").
            // Marker is NOT consumed here: a multi-hit / AoE swing from the armed attacker must stay
            // fully evaded, and its later hits commit through this same pass. Cleared at cue end.
            if (param_5 == 1024 && dodgeMarkerSet)
            {
```

with:

```csharp
            // Dodge finalization: skip the p5=1024 commit for a dodge-resolved slot WITHOUT
            // setting LastParriedTargetMask (that would draw a second "PARRIED" text over "DODGE").
            // Marker is NOT consumed here: a multi-hit / AoE swing from the armed attacker must stay
            // fully evaded, and its later hits commit through this same pass. It is cleared in
            // clear_awaiting_turn_end, next to its parry twin. The attacker check is what stops a
            // surviving marker from swallowing an unrelated attacker's commit.
            if (param_5 == 1024
                && DodgeCommitGate.ShouldSkipCommit(
                    _optionDodgeEnabled, dodgeMarkerSet, _dodgeArmedAttackerId, (byte)param_1))
            {
```

Leave the body of the `if` (lines 1163-1167) untouched.

- [ ] **Step 8: Verify the build**

Run: `.\build.cmd verify`
Expected: PASS, no new warnings.

- [ ] **Step 9: Commit**

Use the **PowerShell tool**, not Bash:

```powershell
git add src/combat/DodgeCommitGate.cs tests/Parry.Tests/DodgeCommitGateTests.cs tests/Parry.Tests/Parry.Tests.csproj src/ParryModule.Combat.cs src/ParryModule.Hooks.cs
git commit -m @'
fix(combat): clear the dodge marker at cue end and bind its commit skip to the attacker

clear_awaiting_turn_end cleared _parryResolvedAtImpactMask but never its dodge
twin, which is only reset when the mod is switched off. A character therefore
stayed immune to every delayed-finalization commit after its first dodge, and
the p5=1024 skip did not even check the attacker. Over a battle the mask filled
up until all ten slots evaded at once: the log showed 560 skips for 10 dodges.

The decision now lives in the pure DodgeCommitGate so it is covered by tests.
'@
```

**In-game verification (required before Task 2 is trusted):** Ein Kampf, ein Dodge mit einem Charakter, danach ein weiterer Angriff auf denselben Charakter durch denselben und durch einen anderen Gegner. Im Log unter `.workspace/logs/`:
- Erwartung: `skipped … (dodge)` erscheint **nur** für tatsächlich abgewehrte Treffer.
- Gegenprobe: `grep -c "skipped for .* (dodge" <log>` und `grep -ci "perfect dodge" <log>` dürfen nicht mehr um zwei Größenordnungen auseinanderliegen (vorher 560 : 10).

**Regressionsrisiko dieses Tasks — bewusst eingegangen, aber zu beobachten.** Der Marker existiert, damit
verzögert finalisierte Angriffe (Anfunkeln, Blitzra, Hauch) über mehrere Treffer hinweg abgewehrt bleiben.
Wenn ein `p5=1024`-Commit **nach** `clear_awaiting_turn_end` einträfe, wäre der Marker jetzt bereits weg und
der Treffer würde durchschlagen. Im Log der letzten Session laufen alle Commits vor `Cue-` und vor
`Enemy action resolved; parry context cleared.` — die Reihenfolge stimmt also. Prüfe bei der In-Game-Messung
gezielt einen Mehrfachtreffer-Zauber (Blitzra) und stelle sicher, dass **kein** Schaden durchkommt, nachdem
der erste Treffer als Dodge quittiert wurde. Schlägt das fehl, ist nicht das Räumen falsch, sondern der
Räumzeitpunkt: dann gehört `_dodgeResolvedAtImpactMask = 0` in `end_parry_window` oder an den Turn-Wechsel,
nicht ans Cue-Ende. Nicht raten — messen und die Spec ergänzen.

---

### Task 2: Overlay-Anker auf die Damage-Number-Position umstellen

**Files:**
- Create: `src/overlay/OverlayAnchorMath.cs`
- Create: `tests/Parry.Tests/OverlayAnchorMathTests.cs`
- Modify: `tests/Parry.Tests/Parry.Tests.csproj`
- Modify: `src/ParryModule.Overlay.cs:17` (Konstante), `:19-30` (Enum), `:331-381` (Anker), `:383-580` (toter Pfad)

**Interfaces:**
- Consumes: nichts aus Task 1.
- Produces:
  - `public const float OverlayAnchorMath.VirtualWidth` = `512f`
  - `public const float OverlayAnchorMath.VirtualHeight` = `416f`
  - `public static bool OverlayAnchorMath.IsBehindCamera(float sentinel)`
  - `public static bool OverlayAnchorMath.IsWithinVirtualViewport(int virtX, int virtY)`
  - `public static Vector2 OverlayAnchorMath.ToScreen(int virtX, int virtY, Vector2 displaySize)`

**Hintergrund (aus der Spec, hier nicht neu herleiten):** `MsCalcCursorPos` (`0x0079f3a0`) schreibt pro `Chr` fünf Feldpaare im 512×416-Raum, jeden Battle-Draw-Frame. `MsNumberDrawProcess` (`0x0079f6c0`) positioniert Schadenszahlen und `MISS` über `Chr+0xf44/0xf48` bzw. — bei gesetztem Popup-Flag `0x80` — über das geclampte `Chr+0xf4c/0xf50` (`FFX.exe.c:848303-848310`). Der Mod liest bisher `0xf34/0xf38`, die rohe ungeclampte Center-Projektion ohne Sentinel.

- [ ] **Step 1: Write the failing test**

Create `tests/Parry.Tests/OverlayAnchorMathTests.cs`:

```csharp
using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for <see cref="OverlayAnchorMath"/>. The engine projects each battle
///     actor into a 512x416 virtual viewport once per battle-draw frame. These helpers
///     interpret that data; keeping them pure lets us test the behind-camera predicate,
///     which previously conflated NaN with a valid coordinate.
/// </summary>
public sealed class OverlayAnchorMathTests
{
    [Fact]
    public void IsBehindCamera_Nan_IsTrue()
    {
        // Reserve party members are not on the field and their projection is uninitialised.
        // The old guard used Math.Abs(x) < 1e6, which is false for NaN only by accident.
        Assert.True(OverlayAnchorMath.IsBehindCamera(float.NaN));
    }

    [Fact]
    public void IsBehindCamera_EngineSentinel_IsTrue()
    {
        // MsCalcCursorPos stores (float)0xfffffe00 when 1/w <= 0.
        Assert.True(OverlayAnchorMath.IsBehindCamera(4.294966e9f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(212f)]
    [InlineData(-317f)]
    public void IsBehindCamera_OrdinaryValues_IsFalse(float sentinel)
    {
        Assert.False(OverlayAnchorMath.IsBehindCamera(sentinel));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(256, 208)]
    [InlineData(512, 416)]
    public void IsWithinVirtualViewport_InsideOrOnEdge_IsTrue(int x, int y)
    {
        Assert.True(OverlayAnchorMath.IsWithinVirtualViewport(x, y));
    }

    [Theory]
    [InlineData(-674, -3)]   // measured from the log with the old 0xf34/0xf38 read
    [InlineData(-185, 40)]
    [InlineData(513, 200)]
    [InlineData(200, 417)]
    public void IsWithinVirtualViewport_Outside_IsFalse(int x, int y)
    {
        Assert.False(OverlayAnchorMath.IsWithinVirtualViewport(x, y));
    }

    [Fact]
    public void ToScreen_ScalesVirtualCoordsToDisplay()
    {
        Vector2 screen = OverlayAnchorMath.ToScreen(256, 208, new Vector2(2560f, 1440f));
        Assert.Equal(1280f, screen.X, 3);
        Assert.Equal(720f, screen.Y, 3);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `.\build.cmd verify`
Expected: FAIL — `error CS0103: The name 'OverlayAnchorMath' does not exist in the current context`.

- [ ] **Step 3: Create the pure anchor math**

Create `src/overlay/OverlayAnchorMath.cs`:

```csharp
using System;
using System.Numerics;

namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Pure helpers for interpreting the per-actor screen projection that
///     <c>MsCalcCursorPos</c> (FFX.exe+0x79F3A0) writes into every <c>Chr</c> once per
///     battle-draw frame, in a 512x416 virtual viewport.
///
///     Kept pure so the behind-camera predicate is testable. The predicate matters:
///     the projection is NaN for reserve members who are not on the field, and a naive
///     <c>Math.Abs(x) &lt; 1e6</c> check only rejects NaN as a side effect of IEEE
///     comparison rules, which made the two cases indistinguishable in the logs.
/// </summary>
public static class OverlayAnchorMath
{
    /// <summary>Width of the engine's virtual battle viewport (TOMakePktScissor 0x200).</summary>
    public const float VirtualWidth = 512f;

    /// <summary>Height of the engine's virtual battle viewport (TOMakePktScissor 0x1a0).</summary>
    public const float VirtualHeight = 416f;

    /// <summary>
    ///     True when the actor has no usable projection: either the engine's
    ///     behind-camera sentinel (<c>(float)0xfffffe00</c>, stored when <c>1/w &lt;= 0</c>)
    ///     or NaN propagated from an uninitialised actor.
    /// </summary>
    public static bool IsBehindCamera(float sentinel)
        => float.IsNaN(sentinel) || MathF.Abs(sentinel) >= 1_000_000f;

    /// <summary>
    ///     True when a projected point lies inside the virtual viewport, edges included.
    ///     Used to decide whether the unclamped engine anchor is usable.
    /// </summary>
    public static bool IsWithinVirtualViewport(int virtX, int virtY)
        => virtX >= 0 && virtX <= (int)VirtualWidth
        && virtY >= 0 && virtY <= (int)VirtualHeight;

    /// <summary>Scales a virtual-viewport point to the current display resolution.</summary>
    public static Vector2 ToScreen(int virtX, int virtY, Vector2 displaySize)
        => new(
            virtX * (displaySize.X / VirtualWidth),
            virtY * (displaySize.Y / VirtualHeight));
}
```

- [ ] **Step 4: Register the new file with the test project**

Modify `tests/Parry.Tests/Parry.Tests.csproj` — add after the `debug\Stage1ProbeFormatter.cs` line, inside the same `<ItemGroup>`:

```xml
    <Compile Include="..\..\src\overlay\OverlayAnchorMath.cs" Link="overlay\OverlayAnchorMath.cs" />
```

- [ ] **Step 5: Run the tests and verify they pass**

Run: `.\build.cmd verify`
Expected: PASS — 11 new tests green.

- [ ] **Step 6: Rewrite the anchor lookup**

Modify `src/ParryModule.Overlay.cs`. Replace the whole body of `try_get_parried_overlay_anchor` from the comment block at line 351 through the `return screen;` at line 380 (keep the method signature at 331 and the three guard blocks at 333-349 exactly as they are):

```csharp
        // MsCalcCursorPos (0x0079f3a0) projects every battle actor through the LIVE Phyre camera
        // once per battle-draw frame (MsBattleCursorCalc -> TODrawBtlWindow) and stores FIVE pairs
        // per Chr, all as int pixels in a 512x416 virtual viewport:
        //   0xf34/0xf38  raw centre projection      — unclamped, no sentinel. Drifts far negative.
        //   0xf3c/0xf40  rounded centre             — 0xf3c carries the behind-camera sentinel
        //   0xf44/0xf48  camera anchor              — what MsNumberDrawProcess uses for damage
        //                                             numbers and MISS (FFX.exe.c:848303-848310)
        //   0xf4c/0xf50  same anchor, clamped to X 27..485, Y 34..391
        // We anchor on 0xf44/0xf48 so our labels land exactly where the engine draws its own
        // numbers, and fall back to the clamped pair when that point leaves the viewport. The
        // engine picks between the two on popup flag 0x80; we have no popup, so we range-test.
        // The legacy ms_camera_matrix VU0 path is dead on PC/HD and was removed with this change.
        const int F3C_Sentinel = 0xf3c;
        const int F44_AnchorX = 0xf44, F48_AnchorY = 0xf48;
        const int F4C_ClampedX = 0xf4c, F50_ClampedY = 0xf50;

        byte* b = (byte*)chr;
        float sentinel = *(float*)(b + F3C_Sentinel);
        if (OverlayAnchorMath.IsBehindCamera(sentinel))
        {
            // NaN and the engine sentinel mean different things (uninitialised actor vs. behind
            // the camera). Both are unusable, but keep them apart in the log so the next reader
            // is not misled the way the previous "behind-camera (f3c=NaN)" line misled us.
            string why = float.IsNaN(sentinel) ? "no-projection (f3c=NaN)" : $"behind-camera (f3c={sentinel:E2})";
            overlay_probe_log($"slot={slotIndex} {why}");
            return null;
        }

        int virtX = *(int*)(b + F44_AnchorX);
        int virtY = *(int*)(b + F48_AnchorY);
        bool clamped = false;
        if (!OverlayAnchorMath.IsWithinVirtualViewport(virtX, virtY))
        {
            virtX = *(int*)(b + F4C_ClampedX);
            virtY = *(int*)(b + F50_ClampedY);
            clamped = true;
        }

        // draw_animated_label centres the glyph box on the anchor (pos = anchor - textSize * 0.5f).
        Vector2 screen = OverlayAnchorMath.ToScreen(virtX, virtY, displaySize);

        overlay_probe_log(
            $"slot={slotIndex} ANCHOR{(clamped ? "(clamped)" : string.Empty)} virt=({virtX},{virtY}) "
            + $"screen=({screen.X:F0},{screen.Y:F0}) disp=({displaySize.X:F0}x{displaySize.Y:F0})");
        return screen;
```

- [ ] **Step 7: Delete the dead VU0 projection path**

Still in `src/ParryModule.Overlay.cs`, delete all of the following. They form one contiguous block plus three declarations near the top; nothing outside this file references them (verified: 36 hits, all in `ParryModule.Overlay.cs`).

Delete line 17:
```csharp
    private const ulong OverlayProjectionRetryFrames = 120;
```

Delete the `OverlayProjectionMode` enum, lines 19-30 in full.

Delete the two fields (search for them; they sit among the other overlay fields):
```csharp
    private OverlayProjectionMode _overlayProjectionMode = OverlayProjectionMode.Unknown;
    private ulong _overlayProjectionLastSuccessFrame;
```

Delete lines 383-580 in their entirety — six methods:
`get_fallback_overlay_anchor`, `try_project_world_to_screen`, `try_read_projection_matrices`, `try_project_with_mode`, `try_project_variant`, `read_matrix`.

Keep the closing brace of the class (line 581).

- [ ] **Step 8: Verify the build**

Run: `.\build.cmd verify`
Expected: PASS. If the compiler reports an unused `using System.Numerics;` for `Matrix4x4`/`Vector3`, leave the using — `Vector2` and `Vector4` still need it. If it reports an unreferenced private member, delete that member too and note it in the commit body.

- [ ] **Step 9: Commit**

Use the **PowerShell tool**:

```powershell
git add src/overlay/OverlayAnchorMath.cs tests/Parry.Tests/OverlayAnchorMathTests.cs tests/Parry.Tests/Parry.Tests.csproj src/ParryModule.Overlay.cs
git commit -m @'
fix(overlay): anchor combat labels where the engine draws its damage numbers

MsCalcCursorPos writes five projected pairs per Chr. We read 0xf34/0xf38, the raw
unclamped centre projection, which has neither a clamp nor a sentinel: at 2560x1440
it produced screen=(-3370,-10), so every label was drawn far off screen or dropped.
MsNumberDrawProcess anchors damage numbers and MISS on 0xf44/0xf48, falling back to
the clamped 0xf4c/0xf50 (FFX.exe.c:848303-848310). We now do the same.

NaN and the behind-camera sentinel are logged as distinct causes; conflating them
hid that the NaN slots were off-field reserve members, not dead characters.

The eight-mode ms_camera_matrix projection is the dead PS2 VU0 path and is removed;
it is why every mode collapsed to screen centre.
'@
```

**In-game verification:** Ein Dodge oder Parry. Das Label muss über dem betroffenen Charakter stehen, an derselben Stelle, an der die Engine Schadenszahlen zeichnet. Im Log: `ANCHOR virt=(…)` mit Werten innerhalb `0..512` / `0..416`.

---

### Task 3: Label-Farben nach Timing-Präzision

**Files:**
- Create: `src/overlay/CombatLabelPalette.cs`
- Create: `tests/Parry.Tests/CombatLabelPaletteTests.cs`
- Modify: `tests/Parry.Tests/Parry.Tests.csproj`
- Modify: `src/ParryModule.Overlay.cs` (`render_parry_window_overlay`, `render_dodge_overlay`, `render_combat_labels`, `draw_animated_label`)

**Interfaces:**
- Consumes: nichts aus Task 1 oder 2.
- Produces:
  - `public static readonly Vector4 CombatLabelPalette.Plain`
  - `public static readonly Vector4 CombatLabelPalette.PreciseTiming`
  - `public static Vector4 CombatLabelPalette.GetFill(bool preciseTiming)`

- [ ] **Step 1: Write the failing test**

Create `tests/Parry.Tests/CombatLabelPaletteTests.cs`:

```csharp
using System.Numerics;
using Xunit;

namespace Fahrenheit.Mods.Parry.Tests;

/// <summary>
///     Unit coverage for <see cref="CombatLabelPalette"/>. DODGE reads as a solid block,
///     PARRIED and PERFECT reward precise timing and both grant the overdrive boost, so
///     they share one tint. The distinction used to exist only in a comment.
/// </summary>
public sealed class CombatLabelPaletteTests
{
    [Fact]
    public void GetFill_SelectsADifferentFillPerTimingClass()
    {
        // The bug this guards against: draw_animated_label received the flag and ignored it,
        // so every label rendered identically. Assert the two branches actually diverge.
        Assert.NotEqual(
            CombatLabelPalette.GetFill(preciseTiming: true),
            CombatLabelPalette.GetFill(preciseTiming: false));
    }

    [Fact]
    public void PreciseTimingFill_IsWarmerThanPlain()
    {
        // "Gold tint" means: at least as much red, and measurably less blue.
        Vector4 gold = CombatLabelPalette.GetFill(preciseTiming: true);
        Vector4 cream = CombatLabelPalette.GetFill(preciseTiming: false);

        Assert.True(gold.X >= cream.X, "gold must not be less red than cream");
        Assert.True(gold.Z < cream.Z, "gold must be less blue than cream");
    }

    [Fact]
    public void PreciseTimingFill_StaysFaint_NotASignalColour()
    {
        // Expedition 33 reserves saturated gold for the Jump flare. Keep every channel bright
        // so the tint reads as a warm cream, never as a signal.
        Vector4 gold = CombatLabelPalette.GetFill(preciseTiming: true);

        Assert.True(gold.X > 0.9f && gold.Y > 0.8f && gold.Z > 0.6f,
            $"tint too saturated to read as cream: {gold}");
    }

    [Fact]
    public void BothFills_AreFullyOpaque()
    {
        // draw_animated_label overwrites W with the animation alpha; a non-opaque constant
        // would silently double-fade the label.
        Assert.Equal(1.0f, CombatLabelPalette.GetFill(preciseTiming: false).W, 3);
        Assert.Equal(1.0f, CombatLabelPalette.GetFill(preciseTiming: true).W, 3);
    }
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `.\build.cmd verify`
Expected: FAIL — `error CS0103: The name 'CombatLabelPalette' does not exist in the current context`.

- [ ] **Step 3: Create the palette**

Create `src/overlay/CombatLabelPalette.cs`:

```csharp
using System.Numerics;

namespace Fahrenheit.Mods.Parry;

/// <summary>
///     Fill colours for the DODGE / PARRIED / PERFECT combat labels.
///
///     Approximated from a Clair Obscur: Expedition 33 screenshot, NOT sampled from the
///     game's assets. Treat the exact channel values as a design choice, not as measured
///     truth.
///
///     The gold tint is deliberately faint. In Expedition 33 a strong gold flare marks
///     the Jump prompt, i.e. an attack that can be neither dodged nor parried; using that
///     same signal colour for a reward would invert its meaning.
/// </summary>
public static class CombatLabelPalette
{
    /// <summary>Warm cream. Used by DODGE — a solid block, no overdrive boost.</summary>
    public static readonly Vector4 Plain = new(0.96f, 0.93f, 0.86f, 1.0f);

    /// <summary>Cream with a gold tint. Used by PARRIED and PERFECT, which both grant the boost.</summary>
    public static readonly Vector4 PreciseTiming = new(0.98f, 0.89f, 0.68f, 1.0f);

    /// <param name="preciseTiming">
    ///     <c>true</c> for PARRIED and PERFECT — a hit answered inside the tight parry window.
    ///     <c>false</c> for a plain DODGE.
    /// </param>
    public static Vector4 GetFill(bool preciseTiming) => preciseTiming ? PreciseTiming : Plain;
}
```

- [ ] **Step 4: Register the new file with the test project**

Modify `tests/Parry.Tests/Parry.Tests.csproj` — add directly before the `overlay\OverlayAnchorMath.cs` line added in Task 2:

```xml
    <Compile Include="..\..\src\overlay\CombatLabelPalette.cs" Link="overlay\CombatLabelPalette.cs" />
```

- [ ] **Step 5: Run the tests and verify they pass**

Run: `.\build.cmd verify`
Expected: PASS — 4 new tests green.

- [ ] **Step 6: Use the palette and rename the flag to say what it means**

Modify `src/ParryModule.Overlay.cs`.

In `draw_animated_label` (line 190), rename the `bool parry` parameter to `bool preciseTiming` and use it. Replace line 196:

```csharp
        Vector4 fill = OverlayTextColor;          // cream/off-white, matching the Exp33 reference
```

with:

```csharp
        Vector4 fill = CombatLabelPalette.GetFill(preciseTiming);
```

In `render_combat_labels` (line 160), rename its `bool parry` parameter to `bool preciseTiming` and pass it through at line 186.

Update the three call sites so the intent is stated once, at the top:

`render_parry_window_overlay`, line 139:
```csharp
        render_combat_labels(_runtime.LastParriedTargetMask, "PARRIED", preciseTiming: true, t, _parriedTextSeed);
```

`render_dodge_overlay`, lines 148-153:
```csharp
        // Slots that evaded inside the tighter parry window read PERFECT and share the parry's
        // gold tint; both grant the overdrive boost. The rest stay DODGE in plain cream. Same
        // timer and seed, so a mixed group animates as one.
        uint perfect = _dodgeTextTargetMask & _dodgeTextPerfectMask;
        uint plain = _dodgeTextTargetMask & ~_dodgeTextPerfectMask;
        if (perfect != 0) render_combat_labels(perfect, "PERFECT", preciseTiming: true, t, _dodgeTextSeed);
        if (plain != 0) render_combat_labels(plain, "DODGE", preciseTiming: false, t, _dodgeTextSeed);
```

Update the header comment of `render_combat_labels` (line 156) so it no longer claims a colour scheme it does not implement:
```csharp
    // Shared animated combat-label renderer. Fill colour comes from CombatLabelPalette:
    // PARRIED and PERFECT take the gold tint, DODGE stays cream. t = 0..1 progress over the
    // label lifetime (ParriedTextSeconds). Each targeted, on-field actor gets one label anchored
    // to its live engine-projected screen position, transformed (pop-in overshoot, squash, skew
    // kick, rotation, whip + float, fade) with two ghost echoes.
```

`OverlayTextColor` (line 5) wurde entgegen der ursprünglichen Annahme in diesem Plan NICHT vom
Status-HUD verwendet — `resolve_parry_state_hud_display()` liest ausschließlich die
`StateText*Color`-Konstanten. Mit dem Wegfall der letzten Verwendung oben (`fill =
OverlayTextColor`) war das Feld tot und wurde entfernt.

- [ ] **Step 7: Verify the build**

Run: `.\build.cmd verify`
Expected: PASS, no unused-parameter warnings.

- [ ] **Step 8: Commit**

Use the **PowerShell tool**:

```powershell
git add src/overlay/CombatLabelPalette.cs tests/Parry.Tests/CombatLabelPaletteTests.cs tests/Parry.Tests/Parry.Tests.csproj src/ParryModule.Overlay.cs
git commit -m @'
feat(overlay): tint PARRIED and PERFECT gold, keep DODGE cream

draw_animated_label received a bool parry flag and never read it, so all three
labels rendered in the same cream while two comments claimed "warm gold" and
"cool blue". The flag now selects a fill from CombatLabelPalette and is renamed
preciseTiming, which is what it actually distinguishes: PARRIED and PERFECT both
answer a hit inside the tight parry window and both grant the overdrive boost.

The tint is faint on purpose. Expedition 33 reserves a strong gold flare for the
Jump prompt, an attack that can be neither dodged nor parried.

Colours are approximated from a screenshot, not sampled; the source file says so.
'@
```

**In-game verification:** Ein Parry und ein einfacher Dodge im selben Kampf. `PARRIED` trägt den Goldstich, `DODGE` bleibt cremeweiß.

---

### Task 4: Magie-Kamera-Unterdrückung separat schaltbar machen

Reine Messvorrichtung. Dieser Task **repariert nichts** — er stellt den Schalter bereit, mit dem wir feststellen, ob die Unterdrückung von `MsAtelRequestMagicCamera` die Magie-VFX frisst.

**Files:**
- Modify: `src/ParryModule.cs` (Feld + Setting-Registrierung)
- Modify: `src/ParryModule.Config.cs` (Persistenz)
- Modify: `src/ParryModule.Settings.cs` (Renderer)
- Modify: `src/ParryModule.Hooks.cs:1339-1344` (Gate)
- Modify: `lang/en-US.json`, `lang/de-DE.json`

**Interfaces:**
- Consumes: nichts aus Tasks 1-3.
- Produces: privates Feld `_optionMagicCameraLock` (`bool`, Default `true` = bisheriges Verhalten).

- [ ] **Step 1: Add the option field**

Modify `src/ParryModule.cs`. Directly below the `_optionBattleCameraLockMode` field declaration, add:

```csharp
    // Splits MsAtelRequestMagicCamera out of the Battle Camera Lock so it can be switched off on
    // its own. Enemy spell casts route their camera through that function; suppressing it without
    // calling orig is suspected of also swallowing the spell VFX. Default true keeps the previous
    // behaviour, so this is a measurement switch, not a fix.
    private bool _optionMagicCameraLock = true;
```

- [ ] **Step 2: Persist the option**

Modify `src/ParryModule.Config.cs`, three places.

In `PersistedSettings`, directly after the `BattleCameraLockMode` property:
```csharp
        public bool? MagicCameraLock { get; set; }
```

In `load_persistent_settings`, directly after the `BattleCameraLockMode` / `EnemyCameraLock` migration block (after the closing brace of the `else if`):
```csharp
            if (persisted.MagicCameraLock.HasValue) _optionMagicCameraLock = persisted.MagicCameraLock.Value;
```

In `persist_settings`, inside the `PersistedSettings payload = new()` initializer, directly after the `BattleCameraLockMode` entry:
```csharp
                MagicCameraLock = _optionMagicCameraLock,
```

- [ ] **Step 3: Gate the hook on the new option**

Modify `src/ParryModule.Hooks.cs`. In `h_ms_atel_request_magic_camera`, replace lines 1339-1344:

```csharp
        bool shouldSuppress = _optionEnabled && _optionBattleCameraLockMode switch
        {
            BattleCameraLockMode.AllTurns       => isAnyTurnActive,
            BattleCameraLockMode.EnemyTurnsOnly => isEnemyTurnActive,
            _                                    => false,
        };
```

with:

```csharp
        bool shouldSuppress = _optionEnabled && _optionMagicCameraLock && _optionBattleCameraLockMode switch
        {
            BattleCameraLockMode.AllTurns       => isAnyTurnActive,
            BattleCameraLockMode.EnemyTurnsOnly => isEnemyTurnActive,
            _                                    => false,
        };
```

Leave `h_ms_atel_request_camera` and `h_ms_battle_special_camera_pause` untouched — that separation is the whole point of this task.

- [ ] **Step 4: Add the settings renderer**

Modify `src/ParryModule.Settings.cs`. Add directly after `render_setting_battle_camera_lock_mode`:

```csharp
    private void render_setting_magic_camera_lock()
    {
        if (ImGui.Checkbox("##fhparry.magic_camera_lock", ref _optionMagicCameraLock))
        {
            persist_settings();
            _enemyMagicCameraLockSuppressCount = 0;
            log_debug($"Magic camera lock = {_optionMagicCameraLock}.");
        }
    }
```

- [ ] **Step 5: Register the renderer**

Modify `src/ParryModule.cs`. In the `FhSettingCustomRenderer` list, add directly after the `battle_camera_lock_mode` entry (line 610):

```csharp
            new FhSettingCustomRenderer("magic_camera_lock", render_setting_magic_camera_lock),
```

- [ ] **Step 6: Add the localisation strings**

Modify `lang/en-US.json`, after the `fhparry.battle_camera_lock_mode.desc` entry:

```json
    "fhparry.magic_camera_lock.name": "Lock Camera During Spells",
    "fhparry.magic_camera_lock.desc": "Also hold the camera when an enemy casts a spell. Turn this off if enemy spell effects stop rendering while the Battle Camera Lock is on — spell casts request their camera through a separate engine path.",
```

Modify `lang/de-DE.json`, at the same position. Follow the file's existing ASCII convention (no umlauts):

```json
    "fhparry.magic_camera_lock.name": "Kamera bei Zaubern sperren",
    "fhparry.magic_camera_lock.desc": "Haelt die Kamera auch dann, wenn ein Gegner zaubert. Abschalten, falls gegnerische Zaubereffekte bei aktiver Kampfkamera-Sperre nicht mehr dargestellt werden — Zauber fordern ihre Kamera ueber einen eigenen Engine-Pfad an.",
```

- [ ] **Step 7: Verify the build**

Run: `.\build.cmd verify`
Expected: PASS. `verify` prüft auch die JSON-Konfiguration (`JSON configuration checks passed.`), also fällt ein Syntaxfehler in den Sprachdateien hier auf.

- [ ] **Step 8: Commit**

Use the **PowerShell tool**:

```powershell
git add src/ParryModule.cs src/ParryModule.Config.cs src/ParryModule.Settings.cs src/ParryModule.Hooks.cs lang/en-US.json lang/de-DE.json
git commit -m @'
feat(camera): make the magic-camera suppression separately switchable

h_ms_atel_request_magic_camera returns the 0xFF "no camera" sentinel without
calling orig whenever the Battle Camera Lock is active, 12 times in the last
session log. Enemy spell VFX went missing in the same session. The camera request
is part of the ATEL cast sequence, so swallowing it plausibly swallows the effect.

This adds the switch that answers the question and changes nothing else. Default
stays true, i.e. the previous behaviour. No fix until it is measured.
'@
```

**In-game verification (this is the deliverable):** Zwei Kämpfe gegen einen zaubernden Gegner, `Battle Camera Lock` = `Enemy Turns Only`.
1. `Lock Camera During Spells` **an** (Default). Erwartung: `[CameraLock] Suppressed MsAtelRequestMagicCamera` im Log; VFX beobachten.
2. Dasselbe mit dem Schalter **aus**. Erwartung: keine Suppress-Zeile; VFX beobachten; prüfen, ob die Kamera bei Zaubern nun mitfährt.

Ergebnis in `docs/superpowers/specs/2026-07-10-labels-camera-overdrive-design.md` unter „Offene Fragen" nachtragen. Erst danach entsteht ein Fix — er ist **nicht** Teil dieses Plans.

---

## Nicht in diesem Plan

**Custom-Overdrive-Modus (Index `0x11`).** Blockiert: der Offset von `limit_modes_obtained` in `PlySave` ist unbekannt und wird aus der Knowledge-Base beschafft, nicht geraten. Danach gilt die Reihenfolge aus der Spec: Bit setzen → messen, was Excel-Index `0x44` als Namen und Beschreibung liefert → **nur bei Fallback-Text** `MsMenuGetText(1, 0x1044)` und `MsMenuGetHelp` hooken. Eigener Plan, sobald der Offset vorliegt.

**Reparatur des Camera Locks.** Erst nach der Messung aus Task 4.

**`MsSetCameraMatrix` (`0x7c0650`).** `src/data/ExternalMemoryOffsetMap.Runtime.cs:63-72` nennt es den korrekten Freeze-Pfad; die Forensik nennt die von ihm geschriebenen Matrizen tot. Widerspruch ungeklärt, blockiert nichts.
