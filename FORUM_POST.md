# AFK Ultimatum — update: auto-start, start-screen card picking, Grueling Gauntlet mode

Plugin: https://github.com/Ezhuk1/AFK-Ultimatum
ExileApi (PoE 3.28+ / current ExileCore build)

The plugin picks Ultimatum modifiers for you by a per-modifier priority list and
presses the confirm button, with human-like cursor movement. This update adds
full hands-off operation from the altar onwards, plus a mode for Inscribed
Ultimatums where the game chooses the modifiers itself.

---

## What's new

**Auto-start.** The pre-encounter screen (reward preview, encounter line, three
modifier icons, BEGIN) is detected and BEGIN is pressed automatically. That
screen is a *world label pinned to the altar*, not part of `IngameUi`, so it is
located separately from the main panel — see the notes at the end.

**Card picking on the start screen.** With Grueling Gauntlet off, the three
offered modifiers are read and the best one is clicked *before* BEGIN — the
choice locks in once the encounter starts, so the order matters.

**Grueling Gauntlet mode** — described in detail below.

**The Esc pause menu stops the plugin**, and it resumes when you close the menu.
No setting, cannot be turned off: the game is frozen, so nothing the plugin
could click would do anything anyway.

**Two sliders removed** (auto-start distance, unknown-modifier priority). Both
had a narrow useful range and are now fixed in code at 35 units and 20.

---

## Grueling Gauntlet: what it does and why

On an Inscribed Ultimatum with *"Ultimatum modifiers are chosen for you"* there
is nothing to vote on. The priority list does not apply — the game has already
decided. The only decision left each round is **accept or bank**.

With the **Grueling Gauntlet** checkbox on:

1. Card selection and the whole priority list are skipped.
2. Each round the plugin presses **Accept Trial**.
3. If the modifier the game chose is marked as a **stopper**, it presses **Take
   Rewards** instead and ends the run with whatever has been earned.

### Marking stoppers

Every priority slider now has a checkbox in front of it. It does nothing in
normal play — it only matters in Gauntlet mode, where it means *"if the game
picks this, the run is not worth continuing"*.

`Drought` (flasks gain no charges) is ticked out of the box. There is a **Clear
all stoppers** button next to **Reset to defaults**.

Note this is deliberately independent of the `100` priority value: `100` means
"don't pick this card" in normal play, while a ticked checkbox means "abandon
the run" in Gauntlet. Tying them together would have meant runs ending on every
modifier you merely dislike taking.

Matching uses the same longest-substring rule as the priorities, so tiered names
resolve to their own entry — `Quicksand III` ticks Quicksand III, not Quicksand.

### Decision details that matter in practice

- **The banking decision latches.** Right after the Take Rewards click the panel
  briefly shows no cards; without the latch the "no cards on screen" branch
  would press confirm and start the very wave being avoided.
- **Take Rewards is clicked at most 4 times**, then the plugin waits for the
  panel to close. Unlike Accept Trial (a no-op until everyone has voted), a
  stray click there could land on the rewards inventory that opens afterwards.
- **The pause hotkey clears the latch.** Pausing means you are taking over, so
  no earlier decision of the bot's survives it — otherwise it would press Take
  Rewards over your choice to continue once the pause expired.
- **If the chosen card cannot be read**, it errs toward banking when a stopper
  is anywhere on screen. Accepting a stopper round can cost the whole run;
  banking early only costs the rounds after this one.

---

## Notes on the implementation

A few things behave differently from what the API suggests, which may be useful
if you are writing something similar.

**`IngameUi.UltimatumPanel` points at the wrong element** on the current build —
it resolves to the Expedition tab, so `ChoicesPanel` and `ConfirmButton` always
come back `null`. The panel is instead located by content: a visible,
panel-sized child of `IngameUi` whose subtree carries the screen's own labels
(`accept trial`, `take rewards`, `Rewards earned`, …), then cast back to
`UltimatumPanel`. The child indices *inside* the panel are still correct, so
only the root lookup had to change.

**`Actor.Action`'s `Moving` bit and `Actor.isMoving` stay set while the
character is standing still.** Logged repeatedly as `rawAction=4224
flagMoving=True` with zero grid movement. Auto-start waits for the character to
stop (the altar label slides across the screen with the camera while running,
so clicks chase a target that has already moved) — and that wait is based on
grid position only.

**`Game.IsEscapeState` and `EscapeState.IsActive` read `true` during normal
play** — the escape state is always present in the game's state stack. They are
not usable as a "pause menu is open" signal; the menu's own visible UI is.

**`ultimatum` in an entity path is not specific enough.** The encounter's own
monsters live under `Metadata/Monsters/LeagueUltimatum/…`, so an "is the altar
near me" check written that way is true for the entire fight — which had the
plugin pressing the altar's label mid-round, landing clicks in random screen
corners as the label drifted.

---

## Settings summary

| Setting | Default |
|---|---|
| Party Leader (follower mode waits for party votes) | `true` |
| Grueling Gauntlet | `false` |
| Auto-start (press BEGIN) | `true` |
| Pause hotkey / duration | `F` / `6000 ms` |
| Force pick when all avoided | `true` |
| Loot pickup after the encounter | `true` |
| Debug logging | `false` |

Plus the per-modifier priority sliders (1 = always take, 100 = never) and the
Gauntlet stop checkboxes.

Loot tuning stays in code: panel-gone wait 8 s, phase timeout 15 s, click
interval 200 ms, pickup range 100 units, monster click-gate 40 units, walk-away
cancel 150 units.

---

Feedback welcome, especially from anyone running Gauntlet with a different
stopper set. With **Debug logging** on, every decision is traced —
`start card[N] '<mod>' priority=…`, `gauntlet - game picked option[N] …`,
`round is still running, not starting` — which makes it easy to see why the
plugin did what it did.
