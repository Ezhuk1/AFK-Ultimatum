# AFK Ultimatum

**Version: v22**

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
| **Grueling Gauntlet** | For Inscribed Ultimatums where the game chooses the modifiers for you. No card is clicked and the priority list is ignored — the plugin just presses **Accept Trial** each round. If the chosen modifier has its **stop checkbox** ticked in the priority list, it presses **Take Rewards** instead and ends the run. | `false` |
| **Auto-start** | Presses **BEGIN** on the pre-encounter screen, so the Ultimatum starts without a manual click. | `true` |
| **Pause hotkey** | Press to make the bot stop clicking/selecting for the duration below, then auto-resume. | `F` |
| **Pause duration (ms)** | How long the bot stays paused after the hotkey is pressed. | `6000` |
| **Force pick when all avoided** | If all 3 visible cards are set to `100` (never), pick the least-bad one anyway (so you don't get stuck). | `true` |
| **Delay between option and start click (ms)** | Pause between clicking the card and clicking confirm. | `300` |
| **Wait after panel opens before clicking (ms)** | Settling delay so the UI is fully interactive before acting. | `250` |
| **Retry interval while panel stays open (ms)** | How often to re-attempt if the confirm did not register and the panel is still open. | `1500` |
| **Smooth (human-like) mouse movement** | Glide the cursor instead of teleporting it. | `true` |
| **Min mouse move duration (ms)** | Base travel time; far moves take longer than this. | `140` |
| **Random click offset (px)** | Small random offset on the click point for a human feel. | `4` |
| **Debug logging** | Logs click coordinates and selection state to the ExileApi log. | `false` |
| **Loot pickup** | After the ultimatum encounter ends, click visible ground items (uses your in-game loot filter). | `true` |

The bot also stops on its own whenever the **in-game pause menu (Esc)** is open,
and resumes when you close it. That needs no setting and cannot be turned off —
the game is frozen, so nothing the plugin could click would do anything anyway.

Some values are fixed in code rather than exposed as sliders:

- **Auto-start distance — 35 units.** BEGIN is only pressed once the altar is
  this close (the start screen is a world label and stays clickable from across
  the map). Auto-start also waits for the character to stand still.
- **Priority for an unknown modifier — 20.** Applies only to a modifier that is
  not in the list below, i.e. something the game has added since.
- **Loot tuning.** Panel-gone wait 8 s, loot phase timeout 15 s, click interval
  200 ms, pickup range 100 units, monster click-gate 40 units, walk-away cancel
  150 units.

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

### Stop checkboxes (Grueling Gauntlet)

Each slider has a checkbox in front of it. It does nothing in normal play — it
only matters while **Grueling Gauntlet** is enabled, where the game picks the
modifier itself and the priority values are ignored.

- Ticked → when the game picks that modifier, the plugin presses **Take
  Rewards** and ends the run instead of accepting the next trial.
- `Drought` is ticked out of the box (flasks gain no charges, so the run is not
  worth continuing).
- **Clear all stoppers** unticks everything; **Reset to defaults** restores both
  the sliders and the checkboxes.

Note this is independent of the `100` priority value: `100` means "don't pick
this card" in normal play, while a ticked checkbox means "abandon the run" in
Gauntlet mode.

---

## How it works

0. **Auto-start.** Before the encounter runs, the altar shows a small
   pre-encounter panel (reward preview, the encounter line, three modifier
   icons, **BEGIN**). While no main panel is up, the plugin finds that BEGIN
   button and presses it — but only once the altar is within **Auto-start max
   distance**, since that panel is a world label and stays clickable from across
   the map. It is a different panel from the one below — see Notes — and `begin`
   only counts as a match when an ancestor also carries the round timer (`0:00`)
   or an encounter-type line, so other "begin" buttons in the game can't
   trigger it.
1. The plugin locates the in-game Ultimatum panel **by content**: a visible,
   panel-sized child of `IngameUi` whose subtree carries the screen's own
   labels (`accept trial`, `take rewards`, `Rewards earned`, `Current Rewards`,
   `Next Reward`). The found element is wrapped back into ExileApi's
   `UltimatumPanel` type, so the strongly-typed API (`ChoicesPanel`,
   `ConfirmButton`, `Modifiers`, `SelectedChoice`) is used as before.
   `IngameUi.UltimatumPanel` is deliberately **not** used — see below.
2. When the panel becomes visible, it waits `Settle delay` ms.
3. It reads the three offered modifiers and looks up each one's priority.
   - **Grueling Gauntlet mode** short-circuits everything from here on: the
     game has already chosen the modifier, so the plugin only presses **Accept
     Trial** — or **Take Rewards**, if the chosen modifier has its stop
     checkbox ticked in the priority list.
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
- The **pre-encounter (BEGIN) screen is a different panel** from the main one:
  it hangs off the altar's world label, is far smaller (~335×437) and has none
  of the `ACCEPT TRIAL` / `TAKE REWARDS` texts, so the main panel lookup does
  not match it. It is located separately, by its BEGIN button plus an
  encounter-type line in an ancestor.
- Mouse input is performed via `user32` (`SetCursorPos` + `mouse_event`) so the real cursor moves on screen.
- Enable **Debug logging** if you want to see the exact click coordinates and selection checks in the ExileApi log.

---

## Changelog

### v22
- **Fixed: BEGIN was clicked in the middle of a running round.** The "is the
  altar near me" check matched any entity with `ultimatum` in its path, which
  includes the encounter's own monsters (`Metadata/Monsters/LeagueUltimatum/…`).
  Mid-fight it was therefore always true, and the plugin kept pressing the
  altar's world label — which drifts across the screen with the camera, so the
  clicks landed in random corners (`(67,996)`, `(33,548)` in the logs).
  - Monsters are now excluded from that check. Excluding them, rather than
    whitelisting the altar's own metadata path, means a renamed altar object
    cannot silently disable auto-start.
  - Added an independent gate: while any live hostile `LeagueUltimatum` monster
    exists, a round is considered in progress and auto-start does nothing. Two
    separate barriers, so one wrong assumption no longer opens the door.
- **Fixed: the stand-still check never passed.** `Actor.Action`'s `Moving` bit
  and `Actor.isMoving` both stay set while the character is standing still
  (logged: `rawAction=4224 flagMoving=True` with no grid movement). Worse, the
  old code refreshed the position baseline on every frame the flag was set, so
  the position check could never contradict it — auto-start stalled for minutes
  at a time. Grid position is now the only signal; both flags are diagnostic
  output only.
- **Fixed: the Esc pause detector froze the plugin.** `Game.IsEscapeState` and
  `EscapeState.IsActive` read `true` during normal play — the escape state is
  always present in the game's state stack. They had been promoted to the
  primary signal and the plugin sat in "game paused, holding off" forever
  without Esc ever being pressed. Detection is back to the pause menu's own
  visible UI (its `Resume Game` button), and the flags are not consulted.

### v21
- **Two sliders removed from the settings UI**, their values fixed in code:
  - *Auto-start max distance* → **35 units**. The useful range turned out to be
    narrow — too small and you cannot get close enough to the altar's centre,
    too large and the encounter starts while walking in.
  - *Priority used when a modifier is not in the list* → **20**. The list covers
    every known modifier, so this only ever applied to something the game has
    added since, and "take it only if nothing better is offered" is the sane
    answer for an unknown.
- Fixed the settings table in this file: the note about the Esc pause menu had
  been inserted into the middle of it, splitting the table in two.

### v20
- **Auto-start now waits for the character to stand still.** The pre-encounter
  screen is pinned to the altar, so while running it slides across the screen
  with the camera and every click chases a target that has already moved — the
  logs showed BEGIN presses scattered from one side of the screen to the other.
  Nothing is clicked until movement has stopped for 350 ms.
  - Movement is read from the player's `Actor.Action` flag, with a grid-position
    check as a second opinion: the flag can miss a frame, and a stuck "still
    moving" would block auto-start entirely.
  - An unreadable player state counts as "standing still", so this can never
    freeze the plugin.
- **Card selection on the start screen, continued.**
  - The pick is now re-armed per BEGIN button rather than per altar. One altar
    puts up a new screen for each wave, and keying on the altar left the
    "already picked" flag set for all of them — one pick followed by a dozen
    bare BEGIN presses in the log.
  - Fixed a regression from the previous version: the ancestor scan accepted any
    element that cast to `UltimatumChoicePanel`, and returned a list of 42 blank
    modifiers, beating the correct subtree sweep to the answer. It now requires
    a plausible count (≤6) and at least one recognised modifier name, matching
    the checks the sweep already had.
  - Icon-row detection additionally requires the row to be horizontally centred
    on BEGIN (within 180 px). Without it the search wandered off to square-ish
    elements at the screen edge.
  - A card whose name cannot be resolved is no longer a candidate at all. Every
    unreadable card used to score the default priority, so the "best" one was
    simply the first in the row — a random modifier picked in the user's name.
    With nothing recognised the choice is now left to the game.

### v19
- **The in-game pause menu (Esc) now stops the bot.** While `GAME PAUSED` is up
  the plugin does nothing at all: the game is frozen, so no click it could make
  means anything, and clicks aimed at the world would land on the menu instead.
  The panels stay in memory behind the menu and still read as valid, so without
  this the bot kept "working" against a frozen game.
  - Detected through ExileCore's own escape game-state, not by looking for the
    menu element — the element check would not notice.
  - Also aborts mid-action: cursor travel, the loot-hover wait and the moment
    between moving and clicking all bail out if Esc goes up while they run.
  - Logged once on entering and leaving the pause, not every frame.
- **Fixed: auto-start gave up permanently with a larger distance limit.** A
  click aimed at an altar that is on screen but not yet in range registers as a
  move command — the character walks over and the encounter never starts. v18
  remembered that button as "already pressed" forever, so the bot then sat there
  doing nothing in front of a start screen that was still up.
  - The same button is now retried after 6 s instead of being latched for good.
  - Reach is re-checked immediately before the click, not only when the button
    is found: the search runs on its own throttle and the cursor takes time to
    travel, so the character can have moved in between.

### v18
- **Fixed: pausing no longer gets overridden in Grueling Gauntlet.** Pressing the
  pause hotkey mid-click left the plugin latched on "bank this run": the click
  was abandoned before it fired, but the flag stayed set, so after taking over
  and choosing to continue manually the bot kept pressing **Take Rewards** over
  that decision. The log gave it away — `pressed take rewards (0/4)`, a click
  that was counted but never happened.
  - `ClickElement` now reports whether a click actually fired, and the banking
    latch is only kept when it did.
  - The pause hotkey clears the latch outright: pausing means the user is taking
    over, so no earlier decision of the bot's should survive it.
- **Fixed: BEGIN pressed repeatedly on the same screen.** The start screen
  lingers for a moment after the click, and the cooldown alone did not cover it —
  the log showed the same spot clicked five times in ten seconds. The plugin now
  remembers the button it pressed and never presses that same element again; the
  next encounter's altar is a different element, so nothing is lost.
- **Auto-start default distance lowered to 20 units** (was 40). At 40 the altar
  could still be far enough that the encounter started while walking in.

### v17
- **Fixed: auto-start pressed BEGIN from across the map.** The start screen is a
  world label pinned to the altar, so it stays on screen — and clickable — from
  far away, and its screen position slides around as the camera moves. The log
  showed clicks scattered at `(1458,142)`, `(271,871)`, `(1094,497)`: the same
  altar, chased across the screen while walking. That both started encounters
  from a distance and dropped clicks on whatever the drifting label happened to
  cover.
  - BEGIN is now only pressed when the altar is within the auto-start distance
    (a setting at the time, fixed at 35 units in v21), measured from
    the entity the world label is pinned to. If the label carries no usable
    entity, the nearest `ultimatum` object in the entity list is used instead,
    so a change in how labels are wired cannot silently disable auto-start.
  - The "too far" message is throttled to once every 3 s, so walking to the
    arena does not flood the log.

### v16
- **Fixed: auto-start did nothing on most encounters.** The BEGIN button was
  only recognised when the start screen used one of the phrasings taken from the
  in-encounter panel (`Survive`, `Protect the Altar`, …), but the start screen
  words them differently — `Defeat waves of enemies`, `Stand in the Stone
  Circles` — so nothing matched and the encounter was never started.
  - The list now covers those too, and, more importantly, the **round timer**
    (`0:00`) under BEGIN counts as an anchor on its own. That check is
    wording-independent, so an encounter type nobody has screenshotted yet can
    no longer break auto-start.
- **Per-modifier stop checkboxes for Grueling Gauntlet.** Each priority slider
  now has a checkbox next to it. With Grueling Gauntlet on, the plugin presses
  **Take Rewards** and ends the run when the game picks any ticked modifier,
  instead of only `Drought`.
  - `Drought` is ticked by default, so existing behaviour is unchanged.
  - **Clear all stoppers** button next to **Reset to defaults**.
  - Matching goes through the same longest-substring rule as the priorities, so
    tiered names resolve to their own entry (`Quicksand III` ticks Quicksand
    III, not Quicksand).
  - The flags are stored index-aligned with the modifier list and are padded or
    trimmed on use, so a config saved by an older build (no flags, or fewer than
    the current list) still loads and keeps whatever was already ticked.

### v15
- **New setting: Auto-start (on by default).** The Ultimatum's pre-encounter
  screen — reward preview, the encounter line ("Survive / Monsters Enrage after
  a time"), three modifier icons and a **BEGIN** button — is now detected and
  BEGIN is pressed automatically, so the encounter starts without a manual
  click. After that the normal round cycle takes over.
  - This is a **separate, smaller panel** from the main Ultimatum window: it
    hangs off the altar's world label, is well under 600 px wide and carries
    none of the `ACCEPT TRIAL` / `TAKE REWARDS` texts, so the main panel lookup
    neither matches it nor should.
  - `begin` on its own is a weak anchor (the Voyage window has a "begin voyage"
    button), so a match only counts when an ancestor's subtree also carries an
    encounter-type line (`Survive`, `Protect the Altar`, `Exterminate`,
    `Stampede`, `Kill the …`).
  - Checked only when no main panel is up, so it cannot interfere with a
    running encounter. Clicks are rate-limited to one per 2.5 s, the search is
    throttled to twice a second and node-budgeted, and the located button is
    cached. The **Only act when the game window is in the foreground** guard
    applies here too.

### v14
- **New setting: Grueling Gauntlet.** On an Inscribed Ultimatum with
  *"Ultimatum modifiers are chosen for you"* there is nothing to vote on, so
  card selection and the whole priority list are skipped: the plugin just
  presses **Accept Trial** each round.
  - **Drought bails out.** If the modifier the game chose is `Drought` (flasks
    gain no charges), the plugin presses **Take Rewards** instead and banks the
    run rather than starting the next wave.
  - The decision latches: after choosing to bank, the plugin will not press
    Accept even though the panel briefly shows no cards right after the click.
    Take Rewards is clicked at most 4 times, then it waits for the panel to
    close — a stray click there could land on the rewards inventory.
  - If the chosen card cannot be read, it errs on the safe side and banks when
    `Drought` is anywhere on screen: accepting a Drought round can cost the
    whole run, banking early only costs the rounds after this one.
  - `Take Rewards` has no strongly-typed accessor in ExileApi (only
    `ConfirmButton`, which is Accept Trial), so it is located by its label.
  - Priority sliders, party voting and autoloot are untouched and still apply
    with the checkbox off.

### v13
- **Silenced the `Priorities ... is not a supported settings element` warning.**
  ExileCore's settings reflection walked the plain `List<string>` looking for a
  node type it could draw and logged a warning on every load. The list is now
  marked `[IgnoreMenu]` — it is still saved and loaded as before (the sliders
  are drawn by hand in the priority panel). Saved priorities were never
  affected by this; only the log line was.
- **`HotkeyNode` → `HotkeyNodeV2`** for the pause hotkey, clearing the obsolete
  API warning. Existing configs migrate automatically: the old `{"Value": 70}`
  shape is still read by the new node, so your hotkey is preserved.
  The key is still polled via `GetAsyncKeyState` rather than the node's own
  `PressedOnce()`, because the check also runs inside the mouse-travel and
  loot-hover loops, which execute between rendered frames — ExileCore refreshes
  its input once per frame and would not see the key until the click finished.

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
