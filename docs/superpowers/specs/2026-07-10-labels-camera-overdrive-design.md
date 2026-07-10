# Design: Kampf-Labels, Camera-Lock-Isolation und Custom-Overdrive-Modus

Datum: 2026-07-10
Branch: `feat/native-evade-parry`
Status: Entwurf zur Freigabe

## Zusammenfassung

Drei Arbeitsbereiche und ein dabei entdeckter Korrektheitsfehler. Der Fehler geht vor, weil er
Verhaltenstests der übrigen Bereiche wertlos macht.

1. **Bugfix** — `_dodgeResolvedAtImpactMask` wird nie geräumt; Charaktere werden nach ihrem ersten
   Dodge dauerhaft immun.
2. **Overlay-Anker** — die Labels lesen die falschen `Chr`-Offsets und landen außerhalb des Bildes.
3. **Label-Farben** — das vorhandene `parry`-Flag wird nicht ausgewertet; alle Labels sind cremeweiß.
4. **Camera Lock** — Unterdrückung von `MsAtelRequestMagicCamera` isolierbar machen, um den Verdacht
   auf fehlende Magie-VFX zu prüfen. Keine Reparatur auf Verdacht.
5. **Custom-Overdrive-Modus** — Index `0x11`, aufgeladen durch Parry und Perfect Dodge.

## Belegte Ausgangslage

Alle Aussagen unten sind gemessen oder aus der Decompilation zitiert, nicht vermutet.

### Settings-Persistenz ist intakt

Der in der vorigen Session vermutete „0-Byte-Blocker" existiert nicht. `fhparry.config.json` liegt
unter `fahrenheit/state/global/fhparry/`, ist 527 Bytes groß und valide. Die 0-Byte-Dateien
`Fahrenheit.Mods.Parry.ParryModule` gehören Fahrenheit, nicht dem Mod. Log-Beleg: `Settings persisted`
erscheint 4×.

### Der Dodge-Marker leckt

`clear_awaiting_turn_end` (`src/ParryModule.Combat.cs:797`) räumt am Cue-Ende
`_parryResolvedAtImpactMask` (Zeile 813), aber **nicht** `_dodgeResolvedAtImpactMask`. Dieser wird im
gesamten Code nur in `reset_runtime_state` (`src/ParryModule.cs:913`) geräumt, und die läuft
ausschließlich beim Abschalten des Mods (`ParryModule.cs:779`, `ParryModule.Settings.cs:13`).

Der Kommentar über der Skip-Bedingung (`src/ParryModule.Hooks.cs:1159-1160`) behauptet
„Cleared at cue end". Das trifft nicht zu.

Zusätzlich prüft der `p5 == 1024`-Skip (`Hooks.cs:1161`) den Angreifer nicht — anders als der
`p5 == 0`-Pfad (`Hooks.cs:1054`). Ein überlebendes Bit schluckt daher den Schaden **jedes** Angreifers.

Log-Beleg: 560× `MsSetDamageInternal … skipped for … (dodge)` bei nur 10 echten Perfect-Dodge-Events.
Am Kampfende „dodgen" alle zehn Slots gleichzeitig, inklusive Reservebank und Aeons.

### Der Overlay-Anker liest die falschen Felder

`MsCalcCursorPos` (`0x0079f3a0`) schreibt pro `Chr` **fünf** Feldpaare, alle im 512×416-Raum, und läuft
jeden Battle-Draw-Frame (über `MsBattleCursorCalc` → `TODrawBtlWindow`; `TOMakePktScissor(0,0,0x200,0x1a0)`).

| Offset | Inhalt |
|---|---|
| `0xf34 / 0xf38` | rohe Center-Projektion, **unclamped, ohne Sentinel** |
| `0xf3c / 0xf40` | Center gerundet, mit Behind-Camera-Sentinel |
| `0xf44 / 0xf48` | Kamera-Anker, ungeclampt |
| `0xf4c / 0xf50` | derselbe Anker, geclampt auf X 27–485, Y 34–391 |

`MsNumberDrawProcess` (`0x0079f6c0`) wählt für Schadenszahlen und MISS je nach Flag-Bit `0x80` genau
`0xf44/0xf48` oder `0xf4c/0xf50` (`FFX.exe.c:848303-848310`).

`try_get_parried_overlay_anchor` (`src/ParryModule.Overlay.cs:331-381`) liest `0xf34/0xf38`. Messung bei
2560×1440: `virt=(-674,-3)` → `screen=(-3370,-10)`. 145 solcher Zeilen im Log, dazu 140× `f3c=NaN`.

Die NaN-Slots (1, 2, 4, 5) sind **nicht** tote, sondern nicht auf dem Feld befindliche Reservemitglieder.
Slots mit `hp=0` (3, 6) liefern gültige Werte.

`ms_camera_matrix` / `ms_screen_matrix` (`0x02311440` / `0x02311480`) sind der tote PS2-VU0-Pfad. Darauf
beruht die achtfache `OverlayProjectionMode`-Fallunterscheidung (`Overlay.cs:19-30`) — deshalb kollabiert
jeder Modus auf die Bildmitte.

### Die Label-Farben existieren nur im Kommentar

`draw_animated_label` (`src/ParryModule.Overlay.cs:190`) bekommt ein `bool parry` und wertet es nie aus.
Zeile 196 setzt hart `fill = OverlayTextColor`. Die Kommentare in Zeile 148 und 156 („warm gold",
„cool blue") beschreiben eine Absicht, die nie implementiert wurde.

`is_perfect_dodge()` (`src/ParryModule.Combat.cs:547`) und `_dodgeTextPerfectMask` existieren bereits und
funktionieren; `render_dodge_overlay` (`Overlay.cs:150-153`) rendert bereits `"PERFECT"`.

### Overdrive: der Blocker ist gefallen

Log-Messung: die Display-Order-Tabelle bei `FFX.exe+0x88765C` lautet
`02 00 01 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F 10 11 12 13`. **Index `0x11` steht darin.**
Der im Forensik-Memo geplante Menü-Hook auf `FUN_008c2370` entfällt damit.

Menü-Sichtbarkeit verlangt zweierlei: Mitgliedschaft in dieser Tabelle **und** ein gesetztes Bit in
`limit_modes_obtained`. Ersteres ist gegeben.

`apply_overdrive_boost()` (`src/ParryModule.Combat.cs:1049-1080`) ruft bereits nativ `MsLimitUp`
(`0x003b15a0`) und wird sowohl bei Parry als auch bei Perfect Dodge ausgelöst
(`mark_dodge_resolved`, `Combat.cs:562-566`).

## Entwurf

### 1. Bugfix: Dodge-Marker räumen

- `_dodgeResolvedAtImpactMask = 0;` in `clear_awaiting_turn_end`, unmittelbar neben Zeile 813.
- Der `p5 == 1024`-Skip erhält dieselbe Angreifer-Prüfung wie der `p5 == 0`-Pfad.
- Der Kommentar in `Hooks.cs:1159-1160` wird dadurch wahr und bleibt stehen.

**Erfolgskriterium:** Ein Dodge erzeugt genau eine `skipped … (dodge)`-Zeile pro tatsächlich
abgewehrtem Treffer. Die Ratio 560:10 muss verschwinden.

**Rollback:** Zwei Zeilen, isoliert rücknehmbar.

### 2. Overlay-Anker umstellen

- Primärquelle `Chr+0xf44` / `Chr+0xf48`, als `int` gelesen.
- Fallback `Chr+0xf4c` / `Chr+0xf50`, wenn die Primärwerte außerhalb `[0,512] × [0,416]` liegen.
  Diese Ausweichregel ist eine **Entscheidung dieses Mods**, keine Engine-Semantik: die Engine wählt
  zwischen beiden Paaren anhand des Popup-Flag-Bits `0x80`, nicht anhand eines Bereichstests. Wir haben
  kein Popup-Flag und wollen ein Label lieber am Bildrand als gar nicht.
- Guard über `Chr+0xf3c` (float): `Abs ≥ 1e6` **oder** `NaN` ⇒ Label verwerfen. NaN und Behind-Camera
  bleiben getrennte Log-Meldungen, damit die Diagnose nicht wieder konflatiert.
- Skalierung unverändert: `× displaySize.X/512`, `× displaySize.Y/416`.
- `OverlayProjectionMode`, `try_read_projection_matrices`, `try_project_with_mode` und
  `get_fallback_overlay_anchor` ersatzlos entfernen (toter VU0-Pfad, ungenutzter Fallback).

**Erfolgskriterium:** Die Labels stehen über dem jeweiligen Charakter, dort wo die Engine ihre
Schadenszahlen zeichnet.

**Risiko:** niedrig. Reines Lesen, kein Hook, keine Schreibzugriffe.

### 3. Label-Farben

Textnamen bleiben `DODGE`, `PARRIED`, `PERFECT`. Ein Screenshot aus Expedition 33 zeigt „DODGE";
Sekundärquellen, die „EVADE" behaupten, werden verworfen.

`draw_animated_label` wertet das vorhandene `parry`-Flag aus:

| Label | Füllung | Rolle |
|---|---|---|
| `DODGE` | warmes Cremeweiß | solide Verteidigung, kein Overdrive-Boost |
| `PARRIED` | Cremeweiß mit Goldstich | präzises Timing, Overdrive-Boost |
| `PERFECT` | Cremeweiß mit Goldstich | präzises Timing, Overdrive-Boost |

Konkrete Werte, **aus dem Screenshot abgeleitet, nicht pixelgenau gemessen** — im Code exakt so zu
kennzeichnen:

- `CombatLabelPalette.Plain = new(0.96f, 0.93f, 0.86f, 1.0f)` — für `DODGE`
- `CombatLabelPalette.PreciseTiming = new(0.98f, 0.89f, 0.68f, 1.0f)` — für `PARRIED` und `PERFECT`
- Outline unverändert (`OverlayOutlineColor`).

Beides sind **neue Konstanten**. `OverlayTextColor` (`Overlay.cs:5`) wurde entgegen der ursprünglichen
Annahme in diesem Dokument NICHT vom Status-HUD verwendet — `resolve_parry_state_hud_display()` liest
ausschließlich die `StateText*Color`-Konstanten. Mit dem Wegfall der letzten Verwendung (`fill =
OverlayTextColor` in `draw_animated_label`) war das Feld tot und wurde entfernt.

Gold bleibt bewusst dezent. In Expedition 33 signalisiert kräftiges Gold den Jump-Flare, also einen
Angriff, der weder ausgewichen noch pariert werden kann. Eine Signalfarbe umzudeuten wäre schlechter
Stil und schlechte Lesbarkeit.

### 4. Camera Lock isolieren

Neues Setting, das die Unterdrückung von `MsAtelRequestMagicCamera` (`Hooks.cs:1348-1360`) getrennt von
`MsAtelRequestCamera` und `MsBattleSpecialCameraPause` schaltbar macht.

Kein Umbau, keine Reparatur. Der Zweck ist ausschließlich, in einem Kampf mit Magie zwei Fragen zu
beantworten: Kommen die VFX zurück? Bleibt die Kamera trotzdem gelockt?

**Erfolgskriterium:** Beide Fragen sind mit Log und Auge beantwortet. Erst danach entsteht ein Fix.

### 5. Custom-Overdrive-Modus „Riposte" (Index `0x11`)

Aufladung bei Parry und Perfect Dodge über den bestehenden `apply_overdrive_boost()`-Pfad. Kein neuer
Ladepfad, keine zweite Belohnungslogik.

Reihenfolge, strikt einzuhalten:

1. **Forensik zuerst.** Der Offset von `limit_modes_obtained` in `PlySave` ist derzeit **unbekannt**.
   Er wird aus der Knowledge-Base beschafft, nicht geraten.
2. **Bit setzen und messen.** Bit `0x11` in `limit_modes_obtained` setzen, Menü öffnen, protokollieren,
   welchen Namen und welche Beschreibung die Engine für Excel-Index `0x44` liefert.
   `MsGetExcelData` ist bei Out-of-Bounds abgesichert (Fallback-Pointer, kein Crash).
3. **Nur bei Bedarf hooken.** Zeigt Schritt 2 brauchbaren Text, sind null Hooks nötig. Zeigt er den
   Fallback, werden `MsMenuGetText(1, 0x1044)` und `MsMenuGetHelp` mit Index-Guard gehookt.

Der Modus ist bei aktiviertem Setting **von Anfang an verfügbar**, also nicht erlernbar. Das Setting ist
**Default-off**, bis die Menü-Integration einmal im Spiel gesehen wurde (Repo-Regel 10: riskante Pfade
opt-in).

**Risiko:** mittel bis hoch. `MsMenuGetText` bedient das gesamte Menüsystem; unser Handler müsste per
Index-Guard sofort aussteigen. Die erwartete String-Kodierung und Lebensdauer des zurückgegebenen
Pointers sind ungeklärt und in Schritt 3 zu beantworten.

**Rollback:** Setting abschalten; Hooks werden nicht installiert.

## Reihenfolge der Umsetzung

Jeder Schritt ist einzeln baubar, einzeln testbar und einzeln rücknehmbar.

1. Bugfix Dodge-Marker (blockiert jede weitere Verhaltensmessung)
2. Overlay-Anker
3. Label-Farben
4. Camera-Lock-Isolation (Messung, kein Fix)
5. Custom-Overdrive-Modus (erst nach Forensik zum `limit_modes_obtained`-Offset)

Die Schritte 1–3 sind rein lokal und risikoarm. Schritt 4 liefert nur Erkenntnis. Schritt 5 ist der
einzige, der neue Hooks einführen kann, und erst nach zwei Messungen.

## Offene Fragen

- **Ist `MsSetCameraMatrix` (`0x7c0650`) ein funktionierender Kamera-Freeze-Pfad?**
  `src/data/ExternalMemoryOffsetMap.Runtime.cs:63-72` behauptet ja. Die Forensik sagt, die von dieser
  Funktion geschriebenen Matrizen seien der tote VU0-Pfad. Beides kann nicht stimmen. Eigene
  Forensik-Runde, blockiert nichts in dieser Spec.
- **Offset von `limit_modes_obtained` in `PlySave`.** Voraussetzung für Schritt 5.
- **Ist Excel-Index `0x44` mit Text belegt?** Entscheidet, ob Schritt 5 Hooks braucht.
- **String-Kodierung und Pointer-Lebensdauer** für einen etwaigen `MsMenuGetText`-Hook.

## Nicht Teil dieser Spec

- Reparatur des Camera Locks (erst nach der Messung aus Schritt 4).
- Frame-Rasterung der Fairness-Tiers, Early-Buffer-Entscheidung.
- `CLAUDE.md` verweist auf `.workspace/knowledge-base/FINAL_PARRY_SPEC.md`; das Verzeichnis existiert
  nicht. Sollte separat geradegerückt werden.

## Aufgeräumt

`resources/parry.png` (`PARRY!`), `resources/success.png` (`SUCCESS!`) und `resources/toobad.png`
(`TOO BAD!`) waren seit dem Initial-Commit `ded0545` unverändert und nirgends referenziert. Entfernt.
