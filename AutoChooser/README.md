# AFK Ultimatum

An [ExileApi](https://github.com/ExileApi/ExileApi) plugin (PoE 3.28 HUD) that automatically picks one of the three **Ultimatum** reward cards by priority and presses the confirm button — using smooth, human-like mouse movement.

When you enter an Ultimatum encounter, a panel appears with **three option cards in a row** and a single confirm button below them ("Begin" / "Start"). This plugin selects the most desirable card according to your priority list and clicks confirm for you, so you can AFK through Ultimatum waves.

---

## Features

- **Automatic card selection** — picks the visible card with the **lowest** priority value.
- **Per-modifier priority sliders** — 45 Ultimatum modifiers, each with a `1–100` slider.
  - `1` = always take this card.
  - higher = less desirable.
  - `>= Avoid threshold` = never take this card.
- **"Never take" support** — mark undesirable modifiers (e.g. monster buffs) so they are skipped.
- **Human-like cursor** — the mouse glides to the target with eased motion, a slight curved path, randomized travel time (scaled by distance) and a small click jitter, instead of teleporting.
- **Reliable selection** — verifies the card was actually selected and retries once if the game did not register the click.
- **No stray clicks after accept** — once the round is confirmed, the plugin will not act again on the same panel.

---

## Installation

1. Make sure you have a working [ExileApi](https://github.com/ExileApi/ExileApi) installation (PoE 3.28).
2. Copy the compiled `AutoChooser.dll` into:

   ```
   <ExileApi>/Plugins/Compiled/AutoChooser/AutoChooser.dll
   ```

3. Enable **AFK Ultimatum** in the ExileApi plugin list and reload plugins.

> The plugin's display name in ExileApi is **AFK Ultimatum**.

---

## Building from source

Requirements:

- .NET 10 SDK (`net10.0-windows`)
- x64
- `ExileCore.dll` from your ExileApi installation

Build:

```powershell
# Point exapiPackage at the folder that contains ExileCore.dll
$env:exapiPackage = "C:\path\to\ExileApi"
cd AutoChooser
dotnet build -c Debug
```

The build references `ExileCore.dll` via the `$(exapiPackage)` variable and pulls the
following NuGet packages automatically:

- `SharpDX` 4.2.0
- `SharpDX.Mathematics` 4.2.0
- `ImGui.NET` 1.89.7.1
- `Newtonsoft.Json` 13.0.3

The compiled DLL is produced at `AutoChooser/bin/Debug/net10.0-windows/AutoChooser.dll`.

---

## Configuration

Open the plugin settings window inside ExileApi. The following options are available:

| Setting | Description | Default |
|----------|-------------|---------|
| **Enable** | Master on/off switch for the plugin. | `false` |
| **Avoid threshold** | A priority `>=` this value means **never take** that card. | `40` |
| **Force pick when all avoided** | If all 3 visible cards are avoided, pick the best one anyway (so you don't get stuck). | `true` |
| **Default priority** | Priority used for a modifier that is not in the known list. | `20` |
| **Delay between option and start click (ms)** | Pause between clicking the card and clicking confirm. | `300` |
| **Wait after panel opens before clicking (ms)** | Settling delay so the UI is fully interactive before acting. | `250` |
| **Retry interval while panel stays open (ms)** | How often to re-attempt if the confirm did not register and the panel is still open. | `1500` |
| **Smooth (human-like) mouse movement** | Glide the cursor instead of teleporting it. | `true` |
| **Min mouse move duration (ms)** | Base travel time; far moves take longer than this. | `140` |
| **Random click offset (px)** | Small random offset on the click point for a human feel. | `4` |
| **Debug logging** | Logs click coordinates and selection state to the ExileApi log. | `false` |

### Priority sliders

Below the options above is a list of **all 45 Ultimatum modifiers**, each with a
`Priority (1–100)` slider:

- **1** — highest priority, always take.
- The larger the number, the less desirable the option.
- **>= Avoid threshold (40)** — never take this card.

Example setup for Ultimatum:

- Set undesirable monster-buff cards (`Shattered Shield`, `Reduced Recovery`,
  `Stormcaller Runes`, …) to **40 or higher** → they will never be picked.
- Set the cards you want (`Restless Ground`, `Quicksand`, `Ruin`, …) to **1–10**.
- Leave the rest at the default **20** — they get taken only if nothing better is offered.

From the three cards on screen, avoided cards (`>= 40`) are dropped; among the
remaining ones the plugin picks the one with the **smallest** priority value.

---

## How it works

1. The plugin watches the in-game `UltimatumPanel` (strongly-typed ExileApi API).
2. When the panel becomes visible, it waits `Settle delay` ms.
3. It reads the three offered modifiers and looks up each one's priority.
4. It clicks the card with the lowest priority (smooth eased movement + jitter).
   - It checks `panel.SelectedChoice` and retries once if the selection did not register.
5. It clicks the **confirm/start** button, then marks the round as confirmed so no
   extra clicks happen while the panel closes.
6. If the panel is still open afterwards (confirm did not take), it retries every
   `Retry interval` until the panel closes.

Modifier names are matched by **substring** (case-insensitive), so a base name like
`Raging Dead` also matches `Raging Dead IV`.

---

## Known Ultimatum modifiers

These names are pre-populated in the priority list (default priority `20`). Exact
strings depend on your client language and game patch; matching is done by substring.

| Category | Names (English) |
|----------|------------------|
| Ground / DoT / traps | `Choking Miasma`, `Stormcaller Runes`, `Raging Dead`, `Blistering Cold`, `Restless Ground`, `Stalking Ruin`, `Razor Dance`, `Quicksand`, `Blood Altar` |
| Totems | `Totem of Costly Might`, `Totem of Costly Potency` |
| Boss / arena | `The Trialmaster`, `Limited Arena` |
| Ruin | `Ruin` |
| Player debuffs | `Reduced Recovery`, `Lessened Reach`, `Buffs Expire Faster`, `Less Cooldown Recovery`, `Escalating Damage Taken`, `Escalating Monster Speed`, `Profane Monsters`, `Unlucky Criticals`, `Hindering Flasks`, `Drought`, `Ailment and Curse Reflection`, `Lightning Damage from Mana Costs`, `Random Projectiles`, `Treacherous Auras`, `Occasional Impotence`, `Siphoned Charges`, `Impurity`, `Waning Spirit` |
| Monster buffs | `Shattered Shield`, `Unstoppable Monsters`, `Lethal Rare Monsters`, `Shielding Monsters`, `Precise Monsters`, `Overwhelming Monsters`, `Deadly Monsters`, `Prismatic Monsters`, `Resistant Monsters`, `Dexterous Monsters`, `Siphoning Monsters`, `Putrid Monsters`, `Impenetrable Monsters` |

---

## Notes

- Card detection uses the strongly-typed `GameController.IngameState.IngameUi.UltimatumPanel` API.
- Mouse input is performed via `user32` (`SetCursorPos` + `mouse_event`) so the real cursor moves on screen.
- Enable **Debug logging** if you want to see the exact click coordinates and selection checks in the ExileApi log.

---

## Disclaimer

This plugin simulates mouse input to interact with the Ultimatum reward UI. Use it at
your own risk and in accordance with the game's terms of service. It is intended as a
convenience for the Ultimatum reward selection screen, not a full gameplay bot.
