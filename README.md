# AFK Ultimatum

**Version: v12**

An [ExileApi](https://github.com/exApiTools/ExileApi-Compiled) plugin (PoE 3.28 HUD) that automatically picks one of the three **Ultimatum** reward cards by priority and presses the confirm button — using smooth, human-like mouse movement.

When you enter an Ultimatum encounter, a panel appears with **three option cards in a row** and a single confirm button below them ("Begin" / "Start"). This plugin selects the most desirable card according to your priority list and clicks confirm for you, so you can AFK through Ultimatum waves.

---

## Features

- **Automatic card selection** — picks the visible card with the **lowest** priority value.
- **Per-modifier priority sliders** — 69 Ultimatum modifiers (including tiered II/III/IV variants), each with a `1–100` slider.
  - `1` = always take this card.
  - higher = less desirable.
  - `>= Avoid threshold` = never take this card.
- **"Never take" support** — set undesirable modifiers (e.g. monster buffs) to `100` so they are skipped.
- **Human-like cursor** — the mouse glides to the target with eased motion, a slight curved path, randomized travel time (scaled by distance) and a small click jitter, instead of teleporting.
- **Reliable selection** — verifies the card was actually selected and retries once if the game did not register the click.
- **No stray clicks after accept** — once the round is confirmed, the plugin will not act again on the same panel.
- **Autoloot on encounter end** — detects that the encounter really ended and picks up the dropped rewards, respecting your in-game loot filter and party allocation. Gold is skipped (it auto-collects on walk-over anyway).
- **Quiet by default** — the plugin writes nothing to the log unless **Debug logging** is enabled.

---

## Installation

1. Copy the plugin folder (with all its contents) into:

   ```
   {ExileApi-path}\Plugins\Source\
   ```

2. Make sure you have a working [ExileApi](https://github.com/exApiTools/ExileApi-Compiled) installation (PoE 3.28).

3. Enable **AFK Ultimatum** in the ExileApi plugin list and reload plugins.

> The plugin's display name in ExileApi is **AFK Ultimatum**.

---

## Configuration

Open the plugin settings window inside ExileApi. The following options are available:

| Setting | Description | Default |
|----------|-------------|---------|
| **Enable** | Master on/off switch for the plugin. | `false` |
| **Party Leader** | When checked, this client picks the modifier by priority (solo or as the party leader). When unchecked, the plugin is a **follower**: it waits for party votes and clicks the card with the most votes — i.e. the leader's pick, since the leader votes first and the group follows. | `true` |
| **Pause hotkey** | Press to make the bot stop clicking/selecting for the duration below, then auto-resume. | `F` |
| **Pause duration (ms)** | How long the bot stays paused after the hotkey is pressed. | `6000` |
| **Force pick when all avoided** | If all 3 visible cards are set to `100` (never), pick the least-bad one anyway (so you don't get stuck). | `true` |
| **Default priority** | Priority used for a modifier that is not in the known list. | `20` |
| **Delay between option and start click (ms)** | Pause between clicking the card and clicking confirm. | `300` |
| **Wait after panel opens before clicking (ms)** | Settling delay so the UI is fully interactive before acting. | `250` |
| **Retry interval while panel stays open (ms)** | How often to re-attempt if the confirm did not register and the panel is still open. | `1500` |
| **Smooth (human-like) mouse movement** | Glide the cursor instead of teleporting it. | `true` |
| **Min mouse move duration (ms)** | Base travel time; far moves take longer than this. | `140` |
| **Random click offset (px)** | Small random offset on the click point for a human feel. | `4` |
| **Debug logging** | Logs click coordinates and selection state to the ExileApi log. | `false` |
| **Loot pickup** | After the ultimatum encounter ends, click visible ground items (uses your in-game loot filter). | `true` |

Loot tuning is intentionally kept out of the settings UI. Built-in defaults:
panel-gone wait 8 s, loot phase timeout 15 s, click interval 200 ms, pickup
range 100 units, monster click-gate 40 units, walk-away cancel 150 units.

### Priority sliders

Below the options above is a list of **all 69 Ultimatum modifiers** (including tiered II/III/IV variants), each with a
`Priority (1–100)` slider:

- **1** — highest priority, always take this card first.
- Higher numbers are less desirable; **99** is only taken as a last resort.
- **100** — never take this card.

Example setup for Ultimatum:

- Set undesirable monster-buff cards (`Shattered Shield`, `Reduced Recovery`,
  `Stormcaller Runes`, …) to **100** → they will never be picked.
- Set the cards you want (`Restless Ground`, `Quicksand`, `Ruin`, …) to **1–10**.
- Leave the rest at the default **20** — they get taken only if nothing better is offered.

From the three cards on screen, cards set to **100** are dropped; among the
remaining ones the plugin picks the one with the **smallest** priority value.

---

## How it works

1. The plugin locates the in-game Ultimatum panel **by content**: a visible,
   panel-sized child of `IngameUi` whose subtree carries the screen's own
   labels (`accept trial`, `take rewards`, `Rewards earned`, `Current Rewards`,
   `Next Reward`). The found element is wrapped back into ExileApi's
   `UltimatumPanel` type, so the strongly-typed API (`ChoicesPanel`,
   `ConfirmButton`, `Modifiers`, `SelectedChoice`) is used as before.
   `IngameUi.UltimatumPanel` is deliberately **not** used — see below.
2. When the panel becomes visible, it waits `Settle delay` ms.
3. It reads the three offered modifiers and looks up each one's priority.
4. **Leader mode** (`Party Leader` checked): it clicks the card with the lowest
   priority (smooth eased movement + jitter). **Follower mode** (unchecked): it waits
   for party votes and clicks the card with the most votes (the leader's pick).
   - It checks `panel.SelectedChoice` and retries once if the selection did not register.
5. It clicks **confirm/start** on every pass. In a party the button stays disabled
   until everyone has voted, so the click is a no-op until then and succeeds once
   enabled; the round ends when the panel closes and the per-round state resets.
6. If the panel is visible but has **no choice cards** (e.g. "Begin" / "Next wave"
   screen), the plugin clicks confirm automatically.
7. **Autoloot.** The panel closes both when a wave starts and when the encounter
   ends — so after every close the plugin waits until the panel has stayed gone
   (`Panel gone wait`) **and** pickable loot is actually visible on the ground.
   (Monsters are intentionally NOT part of this trigger: in a live map stray
   monsters wander near the arena forever and the loot phase would never start.
   They only gate the clicks themselves.) The loot phase clicks the nearest
   visible, pickable ground label (respecting party allocation, skipping gold —
   it auto-collects on walk-over) every `interval` ms, pauses clicking while
   hostile monsters are within 40 units (close-range threats only, so map
   monsters outside the arena can't stall looting forever), and stops ~2.5 s
   after the last successful click (monster pauses don't count as quiet time),
   on timeout, or if the panel reappears
   (a brief panel flash is debounced and does not cancel looting). When a phase
   ends while the panel is still gone, the plugin goes back to waiting so late
   drops still get picked; everything is cancelled on area change or when you
   walk farther than 150 units from the ultimatum spot. Each click is
   fired only after the game confirms the item under the cursor is targeted
   (label highlighted), so it can't degrade into random walk-here clicks.

Modifier names are matched by **substring** (case-insensitive), so a base name like
`Raging Dead` also matches `Raging Dead IV`.

---

## Known Ultimatum modifiers

These names are pre-populated in the priority list. Tiered variants (II, III, IV) have
separate priority sliders so you can, for example, take tier I-II but skip tier III-IV.
Matching uses the longest (most specific) substring, so `"Blistering Cold IV"` matches
its own entry, not the base `"Blistering Cold"`.

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

- Card detection locates the panel by its own on-screen labels, then uses the
  strongly-typed `UltimatumPanel` API on the located element.
  `GameController.IngameState.IngameUi.UltimatumPanel` is **not** used: on the
  current ExileApi build that property resolves to the wrong element (the
  Expedition tab), so `ChoicesPanel` and `ConfirmButton` always came back
  `null`. The child indices *inside* the panel are still correct, which is why
  only the lookup of the panel root had to change.
- The panel's position in the UI tree is not fixed — it sits among the world
  labels, so its index shifts from map to map. Nothing in the plugin is tied to
  a specific index; the located element is cached and re-validated each frame,
  and a full search runs at most every 250 ms.
- Mouse input is performed via `user32` (`SetCursorPos` + `mouse_event`) so the real cursor moves on screen.
- Enable **Debug logging** if you want to see the exact click coordinates and selection checks in the ExileApi log.

---

## Changelog

### v12
- **Fixed: the Ultimatum was no longer detected after the ExileApi update.**
  `IngameUi.UltimatumPanel` resolves to the wrong element on the new build (it
  lands on the Expedition tab), so `ChoicesPanel` / `ConfirmButton` were always
  `null` and the plugin sat idle through every encounter. The panel is now
  located by its own labels (`accept trial`, `take rewards`, `Rewards earned`,
  `Current Rewards`, `Next Reward`) among the children of `IngameUi`, then cast
  back to `UltimatumPanel`. The child indices inside the panel were never
  wrong, so card selection, party voting and looting are unchanged.
- The located panel is cached and re-validated per frame; a full tree search
  runs at most every 250 ms. No UI index is hardcoded — the panel sits among
  the world labels and its index shifts from map to map.

### v11
- **Loot tuning fully removed from the settings UI.** ExileCore renders public
  node properties even without a `[Menu]` attribute, so the v10 approach (dropping
  the attribute) still showed the sliders. The six loot values (panel-gone 8 s,
  timeout 15 s, interval 200 ms, pickup range 100, monster gate 40, walk-away
  cancel 150) are now plain code constants — the settings window has only the
  **Loot pickup** checkbox.

### v10
- **Gold is no longer clicked.** Gold piles (`Items/Gold/`,
  `Items/Currency/GoldCoin`, or labels reading `123 GOLD`) are skipped — gold
  auto-collects when you walk over it.
- **Quiet by default.** Every log message (including phase transitions and
  click traces) is now written only when **Debug logging** is enabled.
- **Settings UI cleanup.** The loot sliders were removed from the menu; only
  the **Loot pickup** checkbox remains. Defaults are now fixed in code:
  pickup range 100, walk-away cancel 150, monster gate 40, panel-gone wait 8 s,
  timeout 15 s, click interval 200 ms.

### v9
- **Fixed the bot getting stuck on the card screen.** Since v7 the per-round
  reset (`_votedThisRound` etc.) only ran when loot waiting was inactive — but
  loot waiting now spans whole waves, so the flag stayed set from the previous
  vote, the bot never voted on the next panel and kept clicking a disabled
  confirm forever. The reset now also runs while loot waiting is active.
- **Looting stops when you walk away.** The plugin anchors the player position
  when the panel closes and cancels all loot activity once you move farther
  than **Loot max walk distance** (new setting, default 300 units) from the
  ultimatum — no more chasing labels across the map.

### v8
- **Fixed the monster click-gate blocking all loot clicks forever.** The gate
  radius was 200 units — in a live map, stray monsters outside the ultimatum
  circle are virtually always inside that radius, so every click was suppressed
  and the phase died on the quiet-grace without a single click. The gate now
  only counts hostiles within **Loot monster check distance** (new setting,
  default 40 units).
- **Monster pauses no longer kill the loot phase.** While clicks are paused due
  to nearby hostiles, the phase waits them out up to the timeout instead of
  ending after 2.5 s and looping.
- **Better diagnostics.** When clicks are paused, debug logging names the
  nearest hostile and its distance every 2 s.

### v7
- **Fixed the loot phase never starting after the encounter.** Two triggers were
  broken: a single spurious `UltimatumPanel.IsVisible` frame silently cancelled
  loot waiting forever (it is now debounced — only a panel that stays visible
  ~1.5 s counts as the next wave), and the "no hostile monsters within 200 units
  for 4 s" requirement often never holds in a live map (stray monsters wander
  near the arena). The loot phase now starts when the panel has stayed gone
  (`Panel gone wait`) **and** pickable loot is actually visible; monsters only
  pause the clicks, as before. The removed **No-monsters wait** setting is gone
  from the settings UI.
- **Late drops are picked too.** When a loot phase ends (timeout / quiet gap)
  while the panel is still gone, waiting re-arms instead of giving up (5-minute
  cap). All loot state is cancelled on area change so the bot never chases
  stray loot into the next map.
- **Diagnostics.** While waiting, debug logging now prints every 2 s why the
  loot phase has not started yet (panel-gone timer / loot visible / monsters
  nearby).

### v6
- **Fixed loot clicks missing every item.** The loot phase now reads labels via
  `ItemsOnGroundLabelElement.VisibleGroundItemLabels` (the maintained API on this
  ExileCore build, same as PickItV2) which returns `Entity`+`Label`+`ClientRect`
  as one consistent bundle, instead of the old `ItemsOnGroundLabelsVisible`.
- **Click only on a confirmed highlight.** After moving the cursor the plugin now
  waits (up to ~150 ms) until the game actually targets the item
  (`Targetable.isTargeted` / shiny label highlight) before clicking. A blind
  click fired before the highlight lands on unhighlighted ground and just walks
  the character there — that was the "always clicks мимо" bug.
- **Safer target selection.** Labels clamped to the screen edge (off-screen
  items) are skipped, labels that repeatedly fail hover verification are skipped
  after 3 attempts, and the obsolete SharpDX `Input.SetCursorPos` overload was
  replaced with the supported `System.Numerics` one.

### v5
- **Finished autoloot trigger.** The loot phase no longer starts on a fixed 45 s
  timer after the panel closes. Instead it starts when the panel has stayed closed
  (`Panel gone wait`, default 8 s) **and** no hostile monsters have been nearby
  for the calm period (`No-monsters wait`, default 4 s) — so it can't fire
  mid-wave, and it doesn't make you wait forever after the last wave.
- **Safer clicks.** Loot clicks are skipped while hostile monsters are within
  200 units (throttled entity scan), and labels that can't be picked up (party
  allocation) are ignored.
- **Early stop.** The loot phase ends ~2.5 s after the last item disappears
  instead of always running the full timeout, and gives up if monsters never
  clear for 90 s.
- Removed the dead `_panelWasVisible` edge-detect (vote state is already reset
  every frame the panel is hidden).

### v4
- **Loot pickup.** After the Ultimatum panel closes, the bot automatically clicks
  visible ground items (respecting your in-game loot filter) until timeout or no
  items remain. Configurable timeout, interval, and max distance.

### v3.1
- Auto-clicks confirm on modifier-less panels ("Begin" / "Next wave" screens).
- Added tiered modifier variants (II, III, IV) with separate priority sliders.
- `MatchBaseMod` now uses longest-substring matching so `"Blistering Cold IV"` doesn't
  accidentally match the base `"Blistering Cold"` entry.
- Pause hotkey now stops the bot instantly — checked inside mouse movement and sleep loops.

### v2
- **Party play.** Added the **Party Leader** setting:
  - **Checked** (default): you are the leader — the plugin picks the modifier by your
    priority list (solo or as party leader).
  - **Unchecked**: the plugin is a **follower** — it waits for party votes and clicks the
    card with the most votes (the leader's pick, since the leader votes first and the
    group follows).
- **Pause hotkey.** Added a configurable **Pause hotkey** (default `F`) and **Pause
  duration** (default `6000` ms). Pressing it makes the bot stop clicking/selecting for
  the set duration, then auto-resume.
- **Robustness.** Null-safe `GameController.Window` access and a guarded `HandlePanel`
  so a transient error during a round transition can't crash the plugin.
- **ExileApi-update resilience.** Some ExileApi builds no longer populate
  `UltimatumPanel.Modifiers` (or return garbage for `SelectedChoice`). The plugin now
  reads the modifier name from `ChoicesPanel.Modifiers` (`UltimatumModifier.Name`), with
  `Element.Text`/`TextNoTags` subtree fallback, and only trusts `SelectedChoice` when
  it's in a sane range — so priorities keep working after ExileApi updates.

### v3
- **Fixed modifier reading** after ExileApi update — reads names from
  `UltimatumChoicePanel.Modifiers` via `UltimatumModifier.Name` property.
- **Sorted modifier list** alphabetically in the settings UI.
- **Reset to defaults** button in the priority sliders panel.
- **Pause hotkey** now immediately stops all actions (no delayed clicks).

### v0.1
- Initial release: automatic card selection by priority with human-like smooth mouse
  movement, reliable selection, and safe-AFK foreground guard.

---

## Disclaimer

This plugin simulates mouse input to interact with the Ultimatum reward UI. Use it at
your own risk and in accordance with the game's terms of service. It is intended as a
convenience for the Ultimatum reward selection screen, not a full gameplay bot.
