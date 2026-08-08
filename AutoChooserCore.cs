using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using RectangleF = SharpDX.RectangleF;

namespace AutoChooser
{
    public class AutoChooser : BaseSettingsPlugin<AutoChooserSettings>
    {
        private bool _panelActive;
        private bool _votedThisRound;
        private DateTime _lastHandle = DateTime.MinValue;
        private DateTime _panelOpenTime = DateTime.MinValue;
        private DateTime _followerWaitStart = DateTime.MinValue;
        private DateTime _pauseUntil = DateTime.MinValue;
        private readonly Random _rng = new();
        private const int FollowerTimeoutMs = 6000;

        private bool _lootPhaseActive;
        private bool _lootPending;
        private DateTime _lootPendingStart = DateTime.MinValue;
        private DateTime _lootPanelGoneSince = DateTime.MinValue;
        private DateTime _lootPanelBackSince = DateTime.MinValue;
        private DateTime _lootPhaseStart = DateTime.MinValue;
        private DateTime _lastLootClick = DateTime.MinValue;
        private DateTime _lastLootItemSeen = DateTime.MinValue;
        private DateTime _lastMonsterCheck = DateTime.MinValue;
        private DateTime _lastLootPendingLog = DateTime.MinValue;
        private DateTime _lastLootAvailCheck = DateTime.MinValue;
        private bool _lootAvailCache;
        private bool _monstersNearbyCache;
        private uint _lootAreaHash;
        private DateTime _lastLootBlockLog = DateTime.MinValue;
        private System.Numerics.Vector2? _lootAnchor;
        private bool _gamePausedLatch;
        private const int MonsterCheckIntervalMs = 250;
        private const int LootNoItemsGraceMs = 2500;
        private const int LootPendingMaxMs = 300000;
        private const int LootPanelBackDebounceMs = 1500;
        private const int LootAvailCheckIntervalMs = 300;
        private readonly Dictionary<long, int> _lootHoverFailures = new();
        private const int LootHoverTimeoutMs = 150;
        private const int LootHoverPollMs = 10;
        private const int LootMaxHoverFailures = 3;
        private const float LootEdgeMarginPx = 36f;

        // Loot tuning: fixed in code on purpose, not user-configurable.
        private const int LootPanelGoneMs = 8000;
        private const int LootPickupTimeoutMs = 15000;
        private const int LootPickupIntervalMs = 200;
        private const int LootPickupMaxDistance = 100;
        private const int LootMonsterDistance = 40;
        private const int LootMaxWalkDistance = 150;

        public override bool Initialise()
        {
            Name = "AFK Ultimatum";
            return true;
        }

        // All plugin output is gated behind the Debug logging setting.
        private void Log(string message)
        {
            if (Settings.Debug.Value)
            {
                LogMessage(message);
            }
        }

        // Reads an element's visible text, tolerating the reads that throw
        // while the UI is mid-rebuild. Used to recognise the Ultimatum panel by
        // its own labels.
        private static string SafeText(Element e, int max)
        {
            try
            {
                string t = e?.GetText(max);
                if (string.IsNullOrWhiteSpace(t)) t = e?.Text;
                if (string.IsNullOrWhiteSpace(t)) return string.Empty;
                t = Normalize(t);
                return t.Length > max ? t.Substring(0, max) : t;
            }
            catch { return string.Empty; }
        }


        // True while the Esc menu ("GAME PAUSED") is up.
        //
        // NB: Game.IsEscapeState and EscapeState.IsActive are NOT usable for
        // this - the escape state exists in the game's state stack at all times,
        // so both read true even while playing normally. Using them froze the
        // plugin permanently. What is actually distinctive is the menu's own
        // UI, so we look for its "Resume Game" button inside the escape state's
        // own UI root (a tiny subtree, unlike a full IngameUi walk).
        //
        // This check fails OPEN on purpose: anything unreadable means "not
        // paused". A false negative costs a stray click; a false positive stops
        // the plugin dead, which is exactly what went wrong before.
        private bool _gamePausedCache;
        private DateTime _lastPauseCheck = DateTime.MinValue;
        private const int PauseCheckIntervalMs = 200;
        private const string PauseMenuMarker = "resume game";

        // Cheap enough to run on demand: reads two booleans off the game state
        // and skips the UI walk. Used at the click sites, where a 200 ms-stale
        // cached answer would be too slow - the menu can go up mid-action.
        private bool IsGamePausedNow()
        {
            // Deliberately runs the full detection instead of reading the escape
            // state flags: those are always true (see above), so short-cutting
            // through them reports "paused" during normal play. The walk is over
            // the escape state's own UI root, which is small enough to run at a
            // click site.
            return DetectPauseMenu();
        }

        private bool IsGamePaused()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastPauseCheck).TotalMilliseconds < PauseCheckIntervalMs)
            {
                return _gamePausedCache;
            }

            _lastPauseCheck = now;
            bool detected = DetectPauseMenu();

            // One-line trace of what the pause detection actually sees. The
            // previous attempt at this check froze the plugin for a whole
            // session before the logs explained why, so the raw inputs are
            // worth having whenever Debug logging is on.
            if (Settings.Debug.Value && detected != _gamePausedCache)
            {
                bool rawEscapeState = false, rawIsActive = false;
                bool haveRoot = false;
                try { rawEscapeState = GameController?.Game?.IsEscapeState ?? false; } catch { }
                try { rawIsActive = GameController?.Game?.EscapeState?.IsActive ?? false; } catch { }
                try { haveRoot = GameController?.Game?.EscapeState?.UIRoot?.IsValid ?? false; } catch { }
                Log($"AutoChooser[pause]: menuVisible={detected} " +
                    $"(Game.IsEscapeState={rawEscapeState}, EscapeState.IsActive={rawIsActive}, uiRootValid={haveRoot})");
            }

            _gamePausedCache = detected;
            return _gamePausedCache;
        }

        private bool DetectPauseMenu()
        {
            // The menu's own visible UI is the ONLY signal used here. An earlier
            // revision promoted Game.IsEscapeState / EscapeState.IsActive to the
            // primary check on the theory that they track the menu; they do not
            // (both read true during normal play), and the plugin froze with
            // "game paused (Esc menu), holding off" without Esc ever being
            // pressed. Do not reintroduce them.
            try
            {
                var game = GameController?.Game;
                if (game == null) return false;

                var root = game.EscapeState?.UIRoot;
                if (root == null || !root.IsValid) return false;

                return SubtreeHasPauseMarker(root, 0);
            }
            catch
            {
                return false;
            }
        }

        private bool SubtreeHasPauseMarker(Element el, int depth)
        {
            if (el == null || depth > 8) return false;

            long kids = 0;
            try
            {
                if (!el.IsValid) return false;
                kids = el.ChildCount;

                string text = SafeText(el, 40);
                if (text.Length > 0 &&
                    text.IndexOf(PauseMenuMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // The menu's elements linger after it closes, so require a
                    // real on-screen rect rather than mere existence.
                    var r = el.GetClientRect();
                    if (r.Width > 0 && r.Height > 0 && IsOnScreen(r)) return true;
                }
            }
            catch { return false; }

            for (int i = 0; i < kids; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;
                if (SubtreeHasPauseMarker(child, depth + 1)) return true;
            }

            return false;
        }

        // --- Panel location ---------------------------------------------------
        // IngameUi.UltimatumPanel points at the wrong element on this build (it
        // resolves to the Expedition tab), so the panel is located by content
        // instead: a visible, panel-sized top-level child of IngameUi whose
        // subtree carries the Ultimatum texts ("accept trial", "take rewards",
        // "Rewards earned", "Round {n/m}").
        //
        // Only the ROOT pointer is broken. Once the right root is found, the
        // child indices ExileCore uses are still correct on this UI - the
        // confirm button really is at [2][6][0] - so the located element is
        // wrapped back into an UltimatumPanel and all existing logic applies.
        private UltimatumPanel _cachedPanel;
        private long _cachedPanelAddr;
        private DateTime _lastPanelSearch = DateTime.MinValue;
        private const int PanelSearchIntervalMs = 250;
        private const float PanelMinWidth = 600f;
        private const float PanelMinHeight = 300f;

        // Texts that only ever appear on the Ultimatum screen. Matched
        // case-insensitively as substrings, so the "<ultimatumnumber>{5}
        // Rewards earned" markup still matches "rewards earned".
        private static readonly string[] PanelMarkers =
        {
            "accept trial", "take rewards", "rewards earned",
            "current rewards", "next reward"
        };

        private UltimatumPanel ResolveUltimatumPanel(DateTime now)
        {
            // Re-validate the cached element every frame (cheap); only run the
            // full tree search when it has gone stale.
            if (_cachedPanel != null)
            {
                try
                {
                    if (_cachedPanel.IsValid && _cachedPanel.Address == _cachedPanelAddr && LooksLikePanel(_cachedPanel))
                        return _cachedPanel;
                }
                catch { }

                _cachedPanel = null;
                _cachedPanelAddr = 0;
            }

            // Searching the whole UI tree is not free, so it is throttled while
            // no panel is cached.
            if ((now - _lastPanelSearch).TotalMilliseconds < PanelSearchIntervalMs)
                return null;
            _lastPanelSearch = now;

            var ui = GameController?.IngameState?.IngameUi;
            if (ui == null) return null;

            try
            {
                long rc = ui.ChildCount;
                for (int i = 0; i < rc; i++)
                {
                    Element c = null;
                    try { c = ui.GetChildAtIndex(i); } catch { continue; }
                    if (c == null) continue;

                    if (!IsPanelSized(c)) continue;
                    if (!SubtreeHasMarker(c, 0)) continue;

                    var found = c.AsObject<UltimatumPanel>();
                    if (found == null) continue;

                    _cachedPanel = found;
                    _cachedPanelAddr = c.Address;
                    Log($"AutoChooser: ultimatum panel located at IngameUi[{i}] 0x{c.Address:X}.");
                    return found;
                }
            }
            catch (Exception ex)
            {
                Log($"AutoChooser: panel search failed: {ex.Message}");
            }

            return null;
        }

        private bool LooksLikePanel(Element el)
        {
            return IsPanelSized(el) && SubtreeHasMarker(el, 0);
        }

        private static bool IsPanelSized(Element el)
        {
            try
            {
                if (!el.IsValid || !el.IsVisible) return false;
                var r = el.GetClientRect();
                return r.Width >= PanelMinWidth && r.Height >= PanelMinHeight;
            }
            catch { return false; }
        }

        // Depth 6 is enough: the deepest marker on this UI is at [2][6][0][0].
        private bool SubtreeHasMarker(Element el, int depth)
        {
            if (el == null || depth > 6) return false;

            long kids = 0;
            try
            {
                if (!el.IsValid) return false;
                kids = el.ChildCount;

                string text = SafeText(el, 80);
                if (text.Length > 0)
                {
                    string low = text.ToLowerInvariant();
                    for (int m = 0; m < PanelMarkers.Length; m++)
                        if (low.Contains(PanelMarkers[m], StringComparison.Ordinal))
                            return true;
                }
            }
            catch { return false; }

            for (int i = 0; i < kids; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;
                if (SubtreeHasMarker(child, depth + 1)) return true;
            }

            return false;
        }

        // --- Start screen ("BEGIN") -------------------------------------------
        // Before the encounter runs there is a separate, smaller panel: reward
        // preview on top, the encounter line ("Survive / Monsters Enrage after
        // a time"), three modifier icons, and a BEGIN button. It is NOT the
        // main Ultimatum panel - it hangs off the altar's world label, well
        // under 600px wide and without any of the ACCEPT TRIAL / TAKE REWARDS
        // texts - so ResolveUltimatumPanel does not (and should not) match it.
        //
        // "begin" alone is far too weak an anchor (the Voyage window has a
        // "begin voyage" button, for one), so a match only counts when an
        // ancestor's subtree also carries an encounter-type line.
        private DateTime _lastStartSearch = DateTime.MinValue;
        private DateTime _lastStartClick = DateTime.MinValue;
        private DateTime _lastPanelSeen = DateTime.MinValue;
        private long _startedButtonAddr;
        private DateTime _startedButtonAt = DateTime.MinValue;

        // Quiet window after the main panel was last visible before auto-start
        // is allowed to act. Covers the moment the panel closes, when its
        // leftovers still read as a start screen.
        private const int StartAfterPanelQuietMs = 2500;

        // How long the character has to be still before auto-start acts. Short
        // enough not to feel sluggish, long enough to outlast the little drift
        // at the end of a move.
        private const int StartStandStillMs = 350;

        // How long a just-clicked BEGIN is left alone before being retried.
        // Long enough for the start screen to disappear on a successful press,
        // short enough that a click which only moved the character is retried
        // once they have walked into range.
        private const int StartRetrySameButtonMs = 6000;
        private const int StartSearchIntervalMs = 500;
        // Guards against a burst of clicks while the screen lingers. The real
        // protection is the per-button address check below - this is just a
        // floor on how often BEGIN can ever be pressed.
        private const int StartClickCooldownMs = 3000;

        // The start screen is a world label pinned to the altar, so it is on
        // screen — and clickable — from across the map, and its screen position
        // slides around as the camera moves. Pressing it from far away is wrong
        // twice over: the encounter starts while the character is nowhere near
        // the arena, and the click lands on whatever happens to be under that
        // drifting label. So the altar has to be close before BEGIN counts, the
        // same way the loot code gates on distance.
        // Fixed in code rather than exposed as a slider: the useful range turned
        // out to be narrow (too small and you cannot get close enough to the
        // altar's centre, too large and the encounter starts while walking in),
        // and 35 sits comfortably inside it.
        private const int StartMaxAltarDistance = 35;

        // "Too far" repeats every search tick while you walk to the arena, so
        // it is throttled to keep the log readable.
        private DateTime _lastStartFarLog = DateTime.MinValue;
        private const int StartFarLogIntervalMs = 3000;

        private void LogStartGated(string message)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastStartFarLog).TotalMilliseconds < StartFarLogIntervalMs) return;
            _lastStartFarLog = now;
            Log(message);
        }
        private const int StartSearchNodeBudget = 6000;
        private const int StartSearchMaxDepth = 12;

        // Every encounter type the start screen can announce. The first cut of
        // this list only had the wording used on the in-encounter panel
        // ("Survive", "Protect the Altar", ...) and auto-start silently did
        // nothing on the others - the start screen phrases them differently
        // ("Defeat waves of enemies", "Stand in the Stone Circles").
        //
        // Kept deliberately loose: single distinctive words rather than whole
        // sentences, so wording tweaks between encounters don't break it again.
        private static readonly string[] EncounterTypeMarkers =
        {
            "survive",          // Survive / Monsters Enrage after a time
            "defeat waves",     // Defeat waves of enemies
            "stone circles",    // Stand in the Stone Circles
            "protect the altar",
            "exterminate",
            "stampede",
            "kill the",
            "trialmaster"       // the boss round announces itself by name
        };

        // A dismissed panel keeps its element alive on this UI, but the game
        // parks it off-screen (the closed panel's children sat at negative
        // coordinates in the UI dumps). Requiring the button to be inside the
        // window is what stops auto-start from clicking a stale BEGIN.
        private bool IsOnScreen(RectangleF r)
        {
            try
            {
                var window = GameController?.Window;
                if (window == null) return false;

                RectangleF w = window.GetWindowRectangleTimeCache;
                float cx = r.X + r.Width / 2f;
                float cy = r.Y + r.Height / 2f;
                return cx >= 0 && cy >= 0 && cx <= w.Width && cy <= w.Height;
            }
            catch { return false; }
        }

        // --- Start-screen card selection --------------------------------------
        // The pre-encounter screen shows the three offered modifiers as round
        // icons above BEGIN. They carry no text, so the names come from the
        // altar entity's UltimatumTrial component instead - same names the
        // priority list uses. The icons sit in a row directly above the button,
        // in the same order as the component's Modifiers list.
        private bool _startCardPicked;
        private long _startCardAltarAddr;
        private long _startCardButtonAddr;

        private bool TryPickStartCard(Element beginButton, DateTime now, List<string> names, long altarAddr)
        {
            // A new start screen means a fresh choice. Keyed on the BEGIN
            // button's own address rather than the altar's: the same altar puts
            // up a new screen for each wave, and keying on the altar left the
            // "already picked" flag set for all of them - the log showed one
            // pick followed by a dozen bare BEGIN presses.
            long buttonAddr = 0;
            try { buttonAddr = beginButton.Address; } catch { }

            if (buttonAddr != 0 && buttonAddr != _startCardButtonAddr)
            {
                _startCardPicked = false;
                _startCardButtonAddr = buttonAddr;
            }

            if (altarAddr != 0 && altarAddr != _startCardAltarAddr)
            {
                _startCardPicked = false;
                _startCardAltarAddr = altarAddr;
            }

            if (_startCardPicked) return false;

            var icons = FindStartCardIcons(beginButton, names != null && names.Count > 0 ? names.Count : 3);
            if (icons == null || icons.Count == 0)
            {
                // Geometry-based icon detection failed. Dump what the area above
                // BEGIN actually contains so the thresholds can be corrected
                // from real numbers instead of guessed at again.
                Log($"AutoChooser: start screen - modifier icons not found" +
                    $"{(names == null || names.Count == 0 ? "" : $" for {names.Count} modifiers ({string.Join(" | ", names)})")}" +
                    ", starting without a pick.");
                if (Settings.Debug.Value) DumpStartScreenGeometry(beginButton);
                _startCardPicked = true;   // do not retry every pass
                return false;
            }

            // No names from the altar component. Try the same typed read the
            // in-encounter panel uses - that one is exact and costs nothing -
            // and only fall back to hovering the icons if it comes up empty.
            if (names == null || names.Count == 0)
            {
                names = ReadStartScreenModifiersTyped(beginButton);
            }

            if (names == null || names.Count == 0)
            {
                names = ReadModifiersByHover(icons);
                if (names == null || names.Count == 0)
                {
                    Log("AutoChooser: start screen - could not read modifier names at all, starting without a pick.");

                    // Show what was taken for the icon row: if those rects are
                    // the button or its frame, the row detection is what needs
                    // fixing, not the name lookup.
                    if (Settings.Debug.Value)
                    {
                        for (int i = 0; i < icons.Count; i++)
                        {
                            try
                            {
                                var ir = icons[i].GetClientRect();
                                Log($"AutoChooser[cards]: icon[{i}] 0x{icons[i].Address:X} " +
                                    $"({ir.Width:0}x{ir.Height:0}@{ir.X:0},{ir.Y:0}) kids={icons[i].ChildCount}");
                            }
                            catch { }
                        }

                        DumpStartScreenGeometry(beginButton);
                    }

                    _startCardPicked = true;
                    return false;
                }
            }

            int count = Math.Min(icons.Count, names.Count);

            // Never pick blind. With no readable names every card scores the
            // default priority and the "best" one is just the first in the row -
            // a random modifier chosen in the user's name. Better to leave the
            // choice to the game than to make it arbitrarily.
            int known = 0;
            for (int i = 0; i < count; i++)
            {
                if (!string.IsNullOrWhiteSpace(names[i]) && MatchBaseMod(Normalize(names[i])) >= 0) known++;
            }

            if (known == 0)
            {
                Log("AutoChooser: start screen - no modifier name could be resolved, leaving the choice to the game.");
                _startCardPicked = true;
                return false;
            }

            int bestIdx = -1;
            int bestPriority = int.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // An unreadable card is not a candidate: its priority would be
                // a guess, and guessing is what we are avoiding here.
                if (string.IsNullOrWhiteSpace(names[i]) || MatchBaseMod(Normalize(names[i])) < 0)
                {
                    Log($"AutoChooser: start card[{i}] '(unreadable)' - skipped");
                    continue;
                }

                int priority = GetPriority(names[i]);
                Log($"AutoChooser: start card[{i}] '{names[i]}' priority={priority}");

                if (priority >= 100) continue;      // never take
                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    bestIdx = i;
                }
            }

            if (bestIdx < 0)
            {
                if (!Settings.ForcePickWhenAllAvoided.Value)
                {
                    Log("AutoChooser: start screen - every offered modifier is set to never; leaving the choice alone.");
                    _startCardPicked = true;
                    return false;
                }

                // Least-bad fallback, same rule the in-encounter panel uses -
                // still only among the cards we could actually identify.
                for (int i = 0; i < count; i++)
                {
                    if (string.IsNullOrWhiteSpace(names[i]) || MatchBaseMod(Normalize(names[i])) < 0) continue;

                    int priority = GetPriority(names[i]);
                    if (priority < bestPriority)
                    {
                        bestPriority = priority;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0) { _startCardPicked = true; return false; }
            }

            _startCardPicked = true;
            _lastStartClick = now;      // keep BEGIN from firing in the same tick

            if (ClickElement(icons[bestIdx], $"start card[{bestIdx}]"))
            {
                Log($"AutoChooser: start screen - picked '{names[bestIdx]}' (priority {bestPriority}).");
                return true;
            }

            return false;
        }

        // Modifier names for the start screen, straight off the altar entity.
        // The icons themselves carry no text, so there is nothing to read from
        // the UI - and hovering each one to get a tooltip would be far more
        // fragile than reading the component.
        private List<string> ReadStartScreenModifiers(out long altarAddr)
        {
            altarAddr = 0;
            try
            {
                var entities = GameController?.EntityListWrapper?.OnlyValidEntities;
                if (entities == null) return null;

                Entity best = null;
                float bestDist = float.MaxValue;

                foreach (var entity in entities)
                {
                    if (entity == null || !entity.IsValid) continue;

                    UltimatumTrial trial = null;
                    try { trial = entity.GetComponent<UltimatumTrial>(); } catch { }
                    if (trial == null) continue;

                    float dist = entity.DistancePlayer;
                    if (dist >= 0f && dist < bestDist)
                    {
                        bestDist = dist;
                        best = entity;
                    }
                }

                if (best == null)
                {
                    // Expected on this build: the altar interactable does not
                    // expose UltimatumTrial. The hover fallback handles it, so
                    // this is not worth logging on every pass.
                    return null;
                }

                altarAddr = best.Address;

                var mods = best.GetComponent<UltimatumTrial>()?.Modifiers;
                if (mods == null || mods.Count == 0)
                {
                    Log($"AutoChooser: start screen - altar 0x{altarAddr:X} has UltimatumTrial but " +
                        $"{(mods == null ? "Modifiers is null" : "the list is empty")}.");
                    return null;
                }

                var names = new List<string>(3);
                foreach (var m in mods)
                {
                    string name = null;
                    try { name = m?.Name; } catch { }
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        try { name = m?.Id; } catch { }
                    }

                    names.Add(Normalize(name ?? string.Empty));
                }

                return names;
            }
            catch (Exception ex)
            {
                Log($"AutoChooser: start screen - reading modifiers failed: {ex.Message}");
                return null;
            }
        }

        // The in-encounter panel reads its three modifiers straight off
        // ChoicesPanel.Modifiers and that works reliably. The start screen is a
        // different element (a world label, not IngameUi's panel), but it is the
        // same kind of UI underneath - so try casting its ancestors to
        // UltimatumChoicePanel and reading the very same list. Far better than
        // hovering: no cursor movement, no timing, exact names.
        private List<string> ReadStartScreenModifiersTyped(Element beginButton)
        {
            Element node = beginButton;

            for (int up = 0; up < 7 && node != null; up++)
            {
                try
                {
                    var cp = node.AsObject<UltimatumChoicePanel>();
                    var mods = cp?.Modifiers;

                    // A start screen offers three modifiers. Reinterpreting some
                    // unrelated ancestor as a choice panel produced a list of 42
                    // blank entries in the log, which then beat the (correct)
                    // subtree sweep to the answer - so the shape is checked
                    // before the contents are trusted.
                    if (mods != null && mods.Count > 0 && mods.Count <= 6)
                    {
                        var names = new List<string>(mods.Count);
                        bool anyReal = false;

                        foreach (var m in mods)
                        {
                            string name = null;
                            try { name = m?.Name; } catch { }
                            if (string.IsNullOrWhiteSpace(name)) { try { name = m?.Id; } catch { } }
                            name = Normalize(name ?? string.Empty);
                            if (MatchBaseMod(name) >= 0) anyReal = true;
                            names.Add(name);
                        }

                        if (anyReal)
                        {
                            Log($"AutoChooser: start screen - modifiers read from the panel: {string.Join(" | ", names)}");
                            return names;
                        }
                    }
                }
                catch { }

                try { node = node.Parent; } catch { break; }
            }

            // Ancestors gave nothing. The choices object may hang off a sibling
            // branch rather than a parent, so sweep the whole start-screen
            // subtree once: take the topmost ancestor and try every descendant.
            try
            {
                Element top = beginButton;
                for (int up = 0; up < 5; up++)
                {
                    Element parent = null;
                    try { parent = top.Parent; } catch { }
                    if (parent == null) break;
                    top = parent;
                }

                var swept = SweepForChoiceModifiers(top, 0);
                if (swept != null && swept.Count > 0)
                {
                    Log($"AutoChooser: start screen - modifiers found in the subtree: {string.Join(" | ", swept)}");
                    return swept;
                }
            }
            catch { }

            return null;
        }

        private List<string> SweepForChoiceModifiers(Element el, int depth)
        {
            if (el == null || depth > 6) return null;

            try
            {
                if (!el.IsValid) return null;

                var cp = el.AsObject<UltimatumChoicePanel>();
                var mods = cp?.Modifiers;
                if (mods != null && mods.Count > 0 && mods.Count <= 6)
                {
                    var names = new List<string>(mods.Count);
                    bool anyReal = false;

                    foreach (var m in mods)
                    {
                        string name = null;
                        try { name = m?.Name; } catch { }
                        if (string.IsNullOrWhiteSpace(name)) { try { name = m?.Id; } catch { } }
                        name = Normalize(name ?? string.Empty);
                        if (MatchBaseMod(name) >= 0) anyReal = true;
                        names.Add(name);
                    }

                    // Only trust a hit that actually resolves to known modifiers -
                    // a random element reinterpreted as a choice panel yields junk.
                    if (anyReal) return names;
                }

                long kids = el.ChildCount;
                for (int i = 0; i < kids; i++)
                {
                    Element child = null;
                    try { child = el.GetChildAtIndex(i); } catch { continue; }
                    if (child == null) continue;

                    var hit = SweepForChoiceModifiers(child, depth + 1);
                    if (hit != null) return hit;
                }
            }
            catch { }

            return null;
        }

        // Fallback name source: hover each icon and read the tooltip the game
        // shows. Used only when the altar's UltimatumTrial component is not
        // readable. It moves the real cursor, so it runs once per start screen
        // and bails out on pause exactly like the loot hover does.
        private List<string> ReadModifiersByHover(List<Element> icons)
        {
            var names = new List<string>(icons.Count);

            try
            {
                var window = GameController?.Window;
                if (window == null) return null;

                Vector2 topLeft = window.GetWindowRectangleTimeCache.TopLeft;

                for (int i = 0; i < icons.Count; i++)
                {
                    if (DateTime.UtcNow < _pauseUntil || IsGamePausedNow()) return null;

                    RectangleF r = icons[i].GetClientRect();
                    if (r.Width <= 0 || r.Height <= 0) { names.Add(string.Empty); continue; }

                    Vector2 center = r.Center + topLeft;
                    Input.SetCursorPos(new System.Numerics.Vector2(center.X, center.Y));

                    // The log showed all three hovers completing inside a single
                    // millisecond, i.e. the tooltip never had a chance to appear.
                    // Give the game a real frame or two before the first read and
                    // keep polling for a while after.
                    Thread.Sleep(90);

                    string text = string.Empty;
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 600)
                    {
                        text = ReadHoverTooltipText();
                        if (MatchBaseMod(Normalize(text)) >= 0) break;
                        Thread.Sleep(30);
                        if (DateTime.UtcNow < _pauseUntil || IsGamePausedNow()) return null;
                    }

                    names.Add(Normalize(text));
                    Log($"AutoChooser: start card[{i}] hover at ({center.X:0},{center.Y:0}) " +
                        $"rect=({r.Width:0}x{r.Height:0}@{r.X:0},{r.Y:0}) -> '{Normalize(text)}'");

                    // Nothing came back: show what the hover elements actually
                    // contain, so the next fix is based on real text instead of
                    // another guess about where tooltips live. The previous dump
                    // reported the hover slot sitting exactly on BEGIN, which is
                    // why the icon coordinates above are logged too - if they
                    // match the button, the icons were misidentified.
                    if (string.IsNullOrWhiteSpace(text) && Settings.Debug.Value && i == 0)
                    {
                        DumpHoverElements();
                    }
                }

                return names;
            }
            catch (Exception ex)
            {
                Log($"AutoChooser: start screen - hover read failed: {ex.Message}");
                return null;
            }
        }

        // Whatever the game is currently showing as a hover tooltip.
        //
        // Deliberately unfiltered: an earlier version only returned text that
        // already matched a known modifier, so anything phrased differently came
        // back as "" and every card scored the default priority. Now the raw
        // text is returned and the caller decides whether it recognises it.
        private string ReadHoverTooltipText()
        {
            try
            {
                var ingame = GameController?.IngameState;
                if (ingame == null) return string.Empty;

                foreach (var candidate in new[] { ingame.UIHoverTooltip, ingame.UIHover, ingame.UIHoverElement })
                {
                    if (candidate == null) continue;

                    string found = FirstNonEmptyText(candidate, 6);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
            }
            catch { }

            return string.Empty;
        }

        // Longest text found in the subtree - tooltips put the title in one
        // child and the description in another, and the longest line is the one
        // that actually names the modifier.
        private string FirstNonEmptyText(Element el, int depth)
        {
            string best = string.Empty;
            CollectLongestText(el, depth, ref best);
            return best;
        }

        private void CollectLongestText(Element el, int depth, ref string best)
        {
            if (el == null || depth < 0) return;

            long kids = 0;
            try
            {
                if (!el.IsValid) return;
                kids = el.ChildCount;

                string t = SafeText(el, 120);
                if (!string.IsNullOrWhiteSpace(t) && t.Length > best.Length) best = t;
            }
            catch { return; }

            for (int i = 0; i < kids; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;
                CollectLongestText(child, depth - 1, ref best);
            }
        }

        // Diagnostic: what the three hover-related UI slots hold right now.
        private void DumpHoverElements()
        {
            try
            {
                var ingame = GameController?.IngameState;
                if (ingame == null) { Log("AutoChooser[hover]: IngameState null."); return; }

                DumpHoverSlot("UIHoverTooltip", ingame.UIHoverTooltip);
                DumpHoverSlot("UIHover", ingame.UIHover);
                DumpHoverSlot("UIHoverElement", ingame.UIHoverElement);
            }
            catch (Exception ex)
            {
                Log($"AutoChooser[hover]: dump failed: {ex.Message}");
            }
        }

        private void DumpHoverSlot(string label, Element el)
        {
            if (el == null) { Log($"AutoChooser[hover]: {label} = null"); return; }

            try
            {
                var r = el.GetClientRect();
                Log($"AutoChooser[hover]: {label} 0x{el.Address:X} valid={el.IsValid} kids={el.ChildCount} " +
                    $"rect=({r.Width:0}x{r.Height:0}@{r.X:0},{r.Y:0})");

                int n = 0;
                DumpHoverWalk(el, new List<int>(), 0, ref n);
            }
            catch (Exception ex)
            {
                Log($"AutoChooser[hover]: {label} threw {ex.GetType().Name}");
            }
        }

        private void DumpHoverWalk(Element el, List<int> path, int depth, ref int count)
        {
            if (el == null || depth > 6 || count >= 25) return;

            long kids = 0;
            try
            {
                if (!el.IsValid) return;
                kids = el.ChildCount;

                string t = SafeText(el, 90);
                if (!string.IsNullOrWhiteSpace(t))
                {
                    count++;
                    string p = path.Count == 0 ? "root" : string.Join("][", path);
                    Log($"AutoChooser[hover]:    [{p}] '{t}'");
                }
            }
            catch { return; }

            for (int i = 0; i < kids && count < 25; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;
                path.Add(i);
                DumpHoverWalk(child, path, depth + 1, ref count);
                path.RemoveAt(path.Count - 1);
            }
        }

        // Diagnostic for "no modifier data": lists nearby entities whose path
        // mentions ultimatum, with the components they actually expose. Runs
        // only with Debug on, throttled, and only when the read came up empty.
        private DateTime _lastEntityDump = DateTime.MinValue;

        private void DumpNearbyUltimatumEntities()
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastEntityDump).TotalMilliseconds < 5000) return;
            _lastEntityDump = now;

            try
            {
                var entities = GameController?.EntityListWrapper?.OnlyValidEntities;
                if (entities == null) { Log("AutoChooser[trial]: entity list unavailable."); return; }

                int shown = 0;
                foreach (var entity in entities)
                {
                    if (entity == null || !entity.IsValid) continue;

                    float dist = entity.DistancePlayer;
                    if (dist > 60f) continue;

                    string path = entity.Path ?? string.Empty;
                    if (path.IndexOf("ultimatum", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // Probe the components we care about by name rather than
                    // enumerating ComponentList - that property's type drags in
                    // GameOffsets, which the plugin does not reference.
                    string comps;
                    try
                    {
                        var present = new List<string>(4);
                        if (entity.HasComponent<UltimatumTrial>()) present.Add("UltimatumTrial");
                        if (entity.HasComponent<Targetable>()) present.Add("Targetable");
                        if (entity.HasComponent<Render>()) present.Add("Render");
                        comps = present.Count > 0 ? string.Join(",", present) : "(none of the probed ones)";
                    }
                    catch (Exception ex) { comps = "probe failed: " + ex.GetType().Name; }

                    Log($"AutoChooser[trial]: {dist:0}u '{path}' components=[{comps}]");
                    if (++shown >= 6) break;
                }

                if (shown == 0) Log("AutoChooser[trial]: no ultimatum-pathed entity within 60u.");
            }
            catch (Exception ex)
            {
                Log($"AutoChooser[trial]: entity dump failed: {ex.Message}");
            }
        }

        // The three icons form a row just above BEGIN. Rather than guess at
        // indices, collect the button's siblings/cousins that look like a row of
        // equally-sized square icons sitting above it, ordered left to right.
        private List<Element> FindStartCardIcons(Element beginButton, int expected)
        {
            try
            {
                RectangleF beginRect = beginButton.GetClientRect();
                if (beginRect.Width <= 0) return null;

                // Climb to the panel that holds both the icons and the button.
                Element panel = beginButton;
                for (int up = 0; up < 4; up++)
                {
                    Element parent = null;
                    try { parent = panel.Parent; } catch { }
                    if (parent == null) break;
                    panel = parent;

                    var candidates = CollectIconRow(panel, beginRect, expected);
                    if (candidates != null) return candidates;
                }
            }
            catch { }

            return null;
        }

        private List<Element> CollectIconRow(Element panel, RectangleF beginRect, int expected)
        {
            var found = new List<Element>(8);
            CollectIconCandidates(panel, beginRect, found, 0);
            if (found.Count < expected) return null;

            // The three modifier icons are a row of same-size squares. Keep the
            // largest group that shares a size and a y, which drops stray
            // square-ish decorations that happen to sit above the button.
            var best = new List<Element>();
            for (int i = 0; i < found.Count; i++)
            {
                RectangleF a = found[i].GetClientRect();
                var group = new List<Element>();

                for (int j = 0; j < found.Count; j++)
                {
                    RectangleF c = found[j].GetClientRect();
                    if (Math.Abs(c.Width - a.Width) <= 6 &&
                        Math.Abs(c.Height - a.Height) <= 6 &&
                        Math.Abs(c.Y - a.Y) <= 14)
                    {
                        group.Add(found[j]);
                    }
                }

                if (group.Count > best.Count) best = group;
            }

            if (best.Count < expected) return null;

            best.Sort((x, y) => x.GetClientRect().X.CompareTo(y.GetClientRect().X));
            if (best.Count > expected) best.RemoveRange(expected, best.Count - expected);
            return best;
        }

        private void CollectIconCandidates(Element el, RectangleF beginRect, List<Element> into, int depth)
        {
            if (el == null || depth > 6 || into.Count > 40) return;

            long kids = 0;
            try
            {
                if (!el.IsValid) return;
                kids = el.ChildCount;

                var r = el.GetClientRect();
                // Loosened from the first guess (20-70px, childless): on the
                // real screen the icons came out larger and some carry a child
                // node. Still square-ish, still in the band right above BEGIN.
                bool squareish = r.Width >= 16 && r.Width <= 110 &&
                                 Math.Abs(r.Width - r.Height) <= 14;
                bool aboveButton = r.Y + r.Height <= beginRect.Y + 10;
                bool nearButton = beginRect.Y - (r.Y + r.Height) <= 220;

                // The icon row is centred over BEGIN. Without a horizontal
                // bound the search wandered off to whatever square-ish elements
                // happened to sit higher up the screen - the log caught a row
                // picked up at the screen edge, far from the button.
                float rowCentre = r.X + r.Width / 2f;
                float buttonCentre = beginRect.X + beginRect.Width / 2f;
                bool alignedWithButton = Math.Abs(rowCentre - buttonCentre) <= 180f;

                if (squareish && aboveButton && nearButton && alignedWithButton && kids <= 1)
                {
                    into.Add(el);
                    return;
                }
            }
            catch { return; }

            for (int i = 0; i < kids; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;
                CollectIconCandidates(child, beginRect, into, depth + 1);
            }
        }

        // Diagnostic: every element in the start screen's subtree with its rect,
        // relative to BEGIN. Only runs when icon detection failed and Debug is
        // on, so it costs nothing in normal play.
        private void DumpStartScreenGeometry(Element beginButton)
        {
            try
            {
                RectangleF b = beginButton.GetClientRect();
                Log($"AutoChooser[cards]: BEGIN rect=({b.Width:0}x{b.Height:0}@{b.X:0},{b.Y:0})");

                Element panel = beginButton;
                for (int up = 0; up < 4; up++)
                {
                    Element parent = null;
                    try { parent = panel.Parent; } catch { }
                    if (parent == null) break;
                    panel = parent;
                }

                int n = 0;
                DumpStartGeometryWalk(panel, b, new List<int>(), 0, ref n);
                Log($"AutoChooser[cards]: {n} elements listed.");
            }
            catch (Exception ex)
            {
                Log($"AutoChooser[cards]: geometry dump failed: {ex.Message}");
            }
        }

        private void DumpStartGeometryWalk(Element el, RectangleF beginRect, List<int> path, int depth, ref int count)
        {
            if (el == null || depth > 6 || count >= 60) return;

            long kids = 0;
            try
            {
                if (!el.IsValid) return;
                kids = el.ChildCount;

                var r = el.GetClientRect();
                if (r.Width > 0 && r.Height > 0)
                {
                    count++;
                    string p = path.Count == 0 ? "panel" : string.Join("][", path);
                    Log($"AutoChooser[cards]:   [{p}] ({r.Width:0}x{r.Height:0}@{r.X:0},{r.Y:0}) " +
                        $"kids={kids} dyAboveBegin={(beginRect.Y - (r.Y + r.Height)):0} txt='{SafeText(el, 30)}'");
                }
            }
            catch { return; }

            for (int i = 0; i < kids && count < 60; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;
                path.Add(i);
                DumpStartGeometryWalk(child, beginRect, path, depth + 1, ref count);
                path.RemoveAt(path.Count - 1);
            }
        }

        // --- Standing still ---------------------------------------------------
        // The start screen is pinned to the altar, so while the character runs
        // the whole thing slides across the screen with the camera: by the time
        // the cursor arrives the icon has moved on. Acting only once movement
        // has stopped removes that entire class of misclicks.
        //
        // Movement is read from the player's Actor.Action flag, with a position
        // check as a second opinion - the flag can miss the odd frame, and a
        // stale "still moving" would stall auto-start completely.
        private System.Numerics.Vector2 _lastPlayerPos;
        private DateTime _lastPlayerMoved = DateTime.MinValue;
        private DateTime _lastStillDiag = DateTime.MinValue;
        private bool _posInitialised;
        private const float StandStillEpsilon = 0.35f;

        public override void AreaChange(AreaInstance area)
        {
            _lastPlayerMoved = DateTime.MinValue;
            _lastPlayerPos = default;

            // Force a fresh baseline: the new area puts the character somewhere
            // else entirely, and comparing against the old position would read
            // as one enormous step.
            _posInitialised = false;
        }

        private bool IsPlayerStandingStill(DateTime now, int quietMs)
        {
            try
            {
                var player = GameController?.Player;
                if (player == null || !player.IsValid) return false;

                bool movingFlag = false;
                long rawAction = -1;
                bool apiIsMoving = false;
                try
                {
                    var actor = player.GetComponent<Actor>();
                    if (actor != null)
                    {
                        rawAction = (long)actor.Action;
                        movingFlag = (actor.Action & ActionFlags.Moving) != 0;
                        try { apiIsMoving = actor.isMoving; } catch { }
                    }
                }
                catch { }

                var pos = player.GridPosNum;
                float dx = pos.X - _lastPlayerPos.X;
                float dy = pos.Y - _lastPlayerPos.Y;
                bool movedOnGrid = (dx * dx + dy * dy) > (StandStillEpsilon * StandStillEpsilon);

                // Auto-start stalled for minutes on "waiting for the character to
                // stop moving" while the character was demonstrably standing
                // still, so log what each input actually reports before changing
                // the rule. Throttled to match the caller's own 3 s gate.
                if (Settings.Debug.Value &&
                    (now - _lastStillDiag).TotalMilliseconds >= 3000)
                {
                    _lastStillDiag = now;
                    double quietFor = _lastPlayerMoved == DateTime.MinValue
                        ? -1 : (now - _lastPlayerMoved).TotalMilliseconds;
                    Log($"AutoChooser[still]: rawAction={rawAction} flagMoving={movingFlag} " +
                        $"isMoving={apiIsMoving} pos=({pos.X:F2},{pos.Y:F2}) " +
                        $"d=({dx:F2},{dy:F2}) movedOnGrid={movedOnGrid} quietFor={quietFor:F0}ms");
                }

                // Grid position is the ONLY signal. Actor.Action's Moving bit and
                // Actor.isMoving both stick on while the character stands still
                // (logged: rawAction=4224 flagMoving=True with no grid movement),
                // and because the old code refreshed _lastPlayerPos on every
                // frame the flag was set, the position check could never
                // contradict it - auto-start stalled for minutes. Both flags are
                // now diagnostic output only. Do not put them back in this branch.
                if (!_posInitialised)
                {
                    _posInitialised = true;
                    _lastPlayerPos = pos;
                    _lastPlayerMoved = now;
                    return false;
                }

                if (movedOnGrid)
                {
                    _lastPlayerPos = pos;
                    _lastPlayerMoved = now;
                    return false;
                }

                return (now - _lastPlayerMoved).TotalMilliseconds >= quietMs;
            }
            catch
            {
                // Unreadable state must not block the plugin.
                return true;
            }
        }

        private void TryStartUltimatum(DateTime now)
        {
            if (!Settings.AutoStart.Value) return;
            if ((now - _lastStartClick).TotalMilliseconds < StartClickCooldownMs) return;

            // Right after a wave ends the just-closed panel leaves elements
            // behind that still look like a start screen, and the altar is of
            // course still next to us - so the distance check alone lets them
            // through. The log caught it clicking screen centre in the very
            // millisecond the panel closed. A short settle window after any
            // panel activity removes that whole class of false positives.
            if ((now - _lastPanelSeen).TotalMilliseconds < StartAfterPanelQuietMs)
            {
                return;
            }

            // A live ultimatum monster means a round is still running, so there is
            // nothing to start. The panel-quiet and distance gates both missed
            // this: mid-round the panel has been closed for far longer than the
            // settle window, and the altar we are fighting next to is well within
            // range. Without this the log shows BEGIN being pressed repeatedly
            // during the fight.
            if (HasLiveUltimatumMonsters())
            {
                LogStartGated("AutoChooser: start screen found but the round is still running, not starting.");
                return;
            }

            // Wait for the character to actually stop. The whole start screen is
            // pinned to the altar, so while running it slides across the screen
            // with the camera and every click chases a target that has already
            // moved - which is what the scattered click coordinates in the logs
            // were. Standing still makes the geometry stable.
            if (!IsPlayerStandingStill(now, StartStandStillMs))
            {
                LogStartGated("AutoChooser: start screen found, waiting for the character to stop moving.");
                return;
            }

            var button = ResolveStartButton(now);
            if (button == null) return;

            // Modifier names: the altar's UltimatumTrial component would be the
            // clean source, but the logs show the interactable only carries
            // Targetable+Render on this build - the component is not there to
            // read. So this is expected to come back empty and the hover
            // fallback in TryPickStartCard is the real path. Kept because it is
            // free when it does work and is the only non-cursor-moving option.
            var names = ReadStartScreenModifiers(out long altarAddr);

            // Do not hammer the same button, but do not give up on it forever
            // either. A click aimed at a distant altar can register as a move
            // command instead of a button press: the character walks over, the
            // encounter never starts, and a permanent latch would leave the bot
            // waiting for a screen that is still sitting right there. So the
            // same button is retried, just not sooner than this.
            long addr = 0;
            try { addr = button.Address; } catch { }
            if (addr != 0 && addr == _startedButtonAddr &&
                (now - _startedButtonAt).TotalMilliseconds < StartRetrySameButtonMs)
            {
                return;
            }

            // Same safe-AFK guard as everywhere else: never steal the cursor
            // while the user is in another window.
            if (Settings.OnlyWhenGameFocused.Value)
            {
                var window = GameController?.Window;
                if (window == null || !window.IsForeground()) return;
            }

            // Re-check reach immediately before committing. The search runs on
            // its own throttle and the cursor takes time to travel, so the
            // character can be moving the whole while - by the time the click
            // lands the altar may no longer be the one we measured.
            if (!IsAltarWithinReach(button))
            {
                return;
            }

            // Outside Grueling Gauntlet the start screen still offers a choice
            // of three modifiers, and it has to be made before BEGIN - once the
            // encounter starts the pick is locked in. In Gauntlet mode the game
            // chooses for us, so there is nothing to click.
            if (!Settings.GruelingGauntlet.Value && !_startCardPicked)
            {
                if (TryPickStartCard(button, now, names, altarAddr))
                {
                    // Give the UI a moment to register the selection; BEGIN goes
                    // out on the next pass.
                    return;
                }
            }

            _lastStartClick = now;
            if (ClickElement(button, "begin (start screen)"))
            {
                _startedButtonAddr = addr;
                _startedButtonAt = now;
                Log("AutoChooser: start screen detected, pressed begin.");
            }
        }

        private Element ResolveStartButton(DateTime now)
        {
            // Deliberately NOT caching across frames. Elements on this UI stay
            // IsValid and keep their rect after their panel is gone (the closed
            // ultimatum panel behaves exactly that way), so a cached BEGIN
            // button would keep looking clickable long after the encounter
            // started - and every click would land on empty screen. The
            // encounter-type check below is what makes a match trustworthy, so
            // it has to be re-run, not remembered.
            if ((now - _lastStartSearch).TotalMilliseconds < StartSearchIntervalMs) return null;
            _lastStartSearch = now;

            var ui = GameController?.IngameState?.IngameUi;
            if (ui == null) return null;

            int budget = StartSearchNodeBudget;
            return SearchStartButton(ui, 0, ref budget);
        }

        private Element SearchStartButton(Element el, int depth, ref int budget)
        {
            if (el == null || depth > StartSearchMaxDepth || budget <= 0) return null;
            budget--;

            long kids = 0;
            try
            {
                if (!el.IsValid) return null;
                kids = el.ChildCount;

                string text = SafeText(el, 24);
                if (text.Length > 0 && text.Trim().Equals("begin", StringComparison.OrdinalIgnoreCase))
                {
                    // IsVisible is not consulted at all: on this UI it reads
                    // false for panels that are plainly on screen. What decides
                    // is a real rect inside the window plus the encounter-type
                    // check - together they separate a live start screen from
                    // the leftover element of a dismissed one.
                    var r = el.GetClientRect();
                    if (r.Width > 0 && r.Height > 0 && IsOnScreen(r) &&
                        IsUltimatumStartButton(el) && IsAltarWithinReach(el))
                    {
                        // Click the button frame rather than the bare label when
                        // it is a sane size - a bigger target with jitter on.
                        var parent = el.Parent;
                        if (parent != null)
                        {
                            var pr = parent.GetClientRect();
                            if (pr.Width >= r.Width && pr.Width <= 400 && pr.Height >= r.Height && pr.Height <= 120)
                                return parent;
                        }

                        return el;
                    }
                }
            }
            catch { return null; }

            for (int i = 0; i < kids && budget > 0; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;

                var hit = SearchStartButton(child, depth + 1, ref budget);
                if (hit != null) return hit;
            }

            return null;
        }

        // How far the altar this start screen belongs to actually is. World
        // labels carry the entity they are pinned to, so the distance comes
        // straight from it; walking up the ancestors because the entity is set
        // on the label root, not on the "begin" leaf.
        private bool IsAltarWithinReach(Element beginLabel)
        {
            Element node = beginLabel;
            for (int up = 0; up < 8 && node != null; up++)
            {
                Entity ent = null;
                try { ent = node.Entity; } catch { }

                if (ent != null)
                {
                    try
                    {
                        if (ent.IsValid)
                        {
                            float dist = ent.DistancePlayer;
                            if (dist >= 0f && dist <= StartMaxAltarDistance) return true;

                            LogStartGated($"AutoChooser: start screen found but its altar is {dist:0} units away " +
                                $"(limit {StartMaxAltarDistance}), not starting yet.");
                            return false;
                        }
                    }
                    catch { }

                    break;
                }

                try { node = node.Parent; } catch { break; }
            }

            // The label carried no usable entity. Rather than give up (which
            // would disable auto-start entirely) fall back to the entity list:
            // if any ultimatum object is close by, we are standing at an altar.
            return HasUltimatumObjectNearby();
        }

        private bool HasUltimatumObjectNearby()
        {
            try
            {
                var entities = GameController?.EntityListWrapper?.OnlyValidEntities;
                if (entities == null) return false;

                float best = float.MaxValue;
                foreach (var entity in entities)
                {
                    if (entity == null || !entity.IsValid) continue;

                    string path = entity.Path;
                    if (string.IsNullOrEmpty(path)) continue;

                    if (path.IndexOf("ultimatum", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // The encounter's own monsters live under
                    // Metadata/Monsters/LeagueUltimatum/, so matching "ultimatum"
                    // alone made this true for the whole fight - and BEGIN then
                    // got pressed on the altar's world label while it drifted
                    // across the screen with the camera. Excluding monsters
                    // rather than whitelisting the altar path keeps an unknown
                    // altar metadata name from disabling auto-start outright.
                    if (path.IndexOf("/Monsters/", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    float dist = entity.DistancePlayer;
                    if (dist >= 0f && dist < best) best = dist;
                }

                if (best <= StartMaxAltarDistance) return true;

                LogStartGated(best == float.MaxValue
                    ? "AutoChooser: start screen found but no ultimatum object nearby, not starting."
                    : $"AutoChooser: start screen found but the nearest ultimatum object is {best:0} units away " +
                      $"(limit {StartMaxAltarDistance}), not starting yet.");
            }
            catch (Exception ex)
            {
                Log($"AutoChooser: altar distance check failed: {ex.Message}");
            }

            return false;
        }

        // Walk up from the "begin" label and require an encounter-type line
        // somewhere in an ancestor's subtree, so other "begin" buttons in the
        // game's UI can never be mistaken for the Ultimatum start screen.
        private bool IsUltimatumStartButton(Element beginLabel)
        {
            Element node = beginLabel;
            for (int up = 0; up < 5; up++)
            {
                Element parent = null;
                try { parent = node.Parent; } catch { }
                if (parent == null) return false;
                node = parent;

                if (SubtreeLooksLikeStartScreen(node, 0)) return true;
            }

            return false;
        }

        // The encounter-name list above can never be complete - the game has
        // more phrasings than we have screenshots of - so the round timer that
        // sits under BEGIN counts as an anchor too. It is wording-independent,
        // which makes it the more durable of the two checks.
        private static readonly Regex StartTimerPattern = new(@"^\d{1,2}:\d{2}$", RegexOptions.Compiled);

        private bool SubtreeLooksLikeStartScreen(Element el, int depth)
        {
            if (el == null || depth > 4) return false;

            long kids = 0;
            try
            {
                if (!el.IsValid) return false;
                kids = el.ChildCount;

                string text = SafeText(el, 80);
                if (text.Length > 0)
                {
                    string trimmed = text.Trim();
                    if (StartTimerPattern.IsMatch(trimmed)) return true;

                    string low = trimmed.ToLowerInvariant();
                    for (int i = 0; i < EncounterTypeMarkers.Length; i++)
                        if (low.Contains(EncounterTypeMarkers[i], StringComparison.Ordinal))
                            return true;
                }
            }
            catch { return false; }

            for (int i = 0; i < kids; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;
                if (SubtreeLooksLikeStartScreen(child, depth + 1)) return true;
            }

            return false;
        }

        private bool _pauseHotkeyWasPressed;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // Polls the pause hotkey through the OS rather than ExileCore's own
        // input state on purpose: this is also called from inside the mouse
        // travel loop and the loot hover wait, which run between rendered
        // frames. ExileCore refreshes its input once per frame, so a
        // node.PressedOnce() there would not see the key until the current
        // click finished - which is exactly when the pause has to bite.
        private bool CheckPauseHotkey()
        {
            var hk = Settings.PauseHotkey.Value;
            int vk = (int)hk.Key;
            if (vk == 0) return false;

            bool hotkeyDown = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (hotkeyDown && !_pauseHotkeyWasPressed)
            {
                _pauseUntil = DateTime.UtcNow.AddMilliseconds(Settings.PauseDurationMs.Value);
                _panelActive = false;
                _lootPhaseActive = false;
                _lootPending = false;
                _lootPanelGoneSince = DateTime.MinValue;
                _lootPanelBackSince = DateTime.MinValue;
                _lootAnchor = null;
                _votedThisRound = false;
                // Pausing means the user is taking over. Any latched decision -
                // notably "bank this run" - has to go with it, or the bot would
                // override their choice the moment the pause expires.
                _gauntletBanking = false;
                _gauntletBankClicks = 0;
                _lastHandle = DateTime.MinValue;
                _followerWaitStart = DateTime.MinValue;
                _pauseHotkeyWasPressed = true;
                Log($"AutoChooser: paused for {Settings.PauseDurationMs.Value} ms.");
                return true;
            }
            _pauseHotkeyWasPressed = hotkeyDown;
            return false;
        }

        public override void Render()
        {
            if (!Settings.Enable.Value)
            {
                _panelActive = false;
                _lootPhaseActive = false;
                _lootPending = false;
                _lootPanelGoneSince = DateTime.MinValue;
                _lootPanelBackSince = DateTime.MinValue;
                _lootAnchor = null;
                _pauseUntil = DateTime.MinValue;
                _pauseHotkeyWasPressed = false;
                _gauntletBanking = false;
                _gauntletBankClicks = 0;
                return;
            }

            CheckPauseHotkey();

            if (DateTime.UtcNow < _pauseUntil)
            {
                return;
            }

            // The in-game pause menu (Esc) is a hard stop: nothing the plugin
            // could click means anything while the game is frozen, and clicks
            // aimed at the world would land on the menu instead. Panels stay in
            // memory behind it, so without this check the bot happily keeps
            // "working" against a frozen game.
            if (IsGamePaused())
            {
                if (!_gamePausedLatch)
                {
                    _gamePausedLatch = true;
                    Log("AutoChooser: game paused (Esc menu), holding off.");
                }

                return;
            }

            if (_gamePausedLatch)
            {
                _gamePausedLatch = false;
                Log("AutoChooser: game resumed.");
            }

            DateTime now = DateTime.UtcNow;

            var panel = ResolveUltimatumPanel(now);
            bool panelVisible = false;
            try
            {
                panelVisible = panel != null && panel.IsVisible;
            }
            catch (Exception ex)
            {
                Log($"AutoChooser: panel visibility read failed: {ex.Message}");
            }

            if (panelVisible) _lastPanelSeen = now;

            // Loot pickup phase: panel is gone, click visible ground items.
            if (_lootPhaseActive)
            {
                if (panelVisible)
                {
                    _lootPhaseActive = false;
                    return;
                }

                // Don't go hunting stray map loot after leaving the arena.
                if (LootAreaChanged())
                {
                    _lootPhaseActive = false;
                    _lootPending = false;
                    Log("AutoChooser: area changed, loot pickup cancelled.");
                    return;
                }

                if (LeftLootAnchor())
                {
                    _lootPhaseActive = false;
                    _lootPending = false;
                    _lootAnchor = null;
                    Log("AutoChooser: walked away from the ultimatum, loot pickup cancelled.");
                    return;
                }

                // Same safe-AFK guard as panel handling: don't hijack the cursor
                // while you are using another window.
                if (Settings.OnlyWhenGameFocused.Value)
                {
                    var lootWindow = GameController?.Window;
                    if (lootWindow == null || !lootWindow.IsForeground())
                    {
                        return;
                    }
                }

                if ((now - _lootPhaseStart).TotalMilliseconds >= LootPickupTimeoutMs)
                {
                    _lootPhaseActive = false;
                    Log("AutoChooser: loot pickup ended (timeout).");
                    RearmLootPending();
                    return;
                }

                // Nothing to click for a while -> rewards picked up (or none dropped), stop early.
                if ((now - _lastLootItemSeen).TotalMilliseconds >= LootNoItemsGraceMs)
                {
                    _lootPhaseActive = false;
                    Log("AutoChooser: loot pickup ended (no more items).");
                    RearmLootPending();
                    return;
                }

                if ((now - _lastLootClick).TotalMilliseconds >= LootPickupIntervalMs)
                {
                    _lastLootClick = now;
                    if (MonstersNearby(now))
                    {
                        // Hostiles close by: wait them out instead of clicking.
                        // Treat it as activity so the quiet-grace above doesn't kill
                        // the phase while we are deliberately not clicking.
                        _lastLootItemSeen = now;
                        if (Settings.Debug.Value && (now - _lastLootBlockLog).TotalMilliseconds >= 2000)
                        {
                            _lastLootBlockLog = now;
                            Log($"AutoChooser: loot clicks paused - {DescribeNearestHostile()}.");
                        }
                    }
                    else if (TryPickupLoot())
                    {
                        _lastLootItemSeen = now;
                    }
                }

                return;
            }

            // Pending loot: the panel closed after being handled. That happens after
            // every confirmed card (wave start) AND when the encounter ends. The old
            // "no monsters nearby for N seconds" requirement never holds in a live
            // map (stray monsters wander near the arena), so the loot phase now
            // starts once the panel has stayed gone long enough AND pickable loot
            // is actually visible on the ground - monsters only gate the clicks
            // themselves inside the loot phase.
            if (_lootPending)
            {
                if (panelVisible)
                {
                    // Debounce: a one-frame panel flash (reward UI) must not cancel
                    // looting; only a panel that STAYS visible (next wave) does.
                    // While it is visible we fall through so normal card handling
                    // keeps working instead of stalling for the debounce window.
                    if (_lootPanelBackSince == DateTime.MinValue)
                    {
                        _lootPanelBackSince = now;
                    }

                    if ((now - _lootPanelBackSince).TotalMilliseconds >= LootPanelBackDebounceMs)
                    {
                        _lootPending = false;
                        _lootPanelBackSince = DateTime.MinValue;
                        _lootPanelGoneSince = DateTime.MinValue;
                        Log("AutoChooser: panel reappeared - it was an inter-wave close, loot pending cancelled.");
                    }
                    // fall through to panel handling below
                }
                else
                {
                    _lootPanelBackSince = DateTime.MinValue;

                    // The per-round reset that lives in the !panelVisible block below
                    // must also run while loot waiting spans the whole wave - otherwise
                    // _votedThisRound stays true from the last vote, the next panel is
                    // never voted on and the bot gets stuck clicking a disabled confirm.
                    _panelActive = false;
                    _votedThisRound = false;
                    _gauntletBanking = false;
                    _gauntletBankClicks = 0;
                    _lastHandle = DateTime.MinValue;
                    _followerWaitStart = DateTime.MinValue;

                    if (LootAreaChanged())
                    {
                        _lootPending = false;
                        Log("AutoChooser: area changed, loot pending cancelled.");
                        return;
                    }

                    if (LeftLootAnchor())
                    {
                        _lootPending = false;
                        _lootAnchor = null;
                        Log("AutoChooser: walked away from the ultimatum, loot pending cancelled.");
                        return;
                    }

                    if ((now - _lootPendingStart).TotalMilliseconds >= LootPendingMaxMs)
                    {
                        _lootPending = false;
                        _lootPanelGoneSince = DateTime.MinValue;
                        Log("AutoChooser: loot pending cancelled (no lootable items appeared).");
                        return;
                    }

                    bool panelGoneLongEnough = (now - _lootPanelGoneSince).TotalMilliseconds >= LootPanelGoneMs;

                    // Throttled scan: any click-ready ground labels right now?
                    if (panelGoneLongEnough && (now - _lastLootAvailCheck).TotalMilliseconds >= LootAvailCheckIntervalMs)
                    {
                        _lastLootAvailCheck = now;
                        _lootAvailCache = FindBestLootLabel(out _, out _, out _, out _) != null;
                    }

                    bool lootAvailable = panelGoneLongEnough && _lootAvailCache;

                    if (Settings.Debug.Value && (now - _lastLootPendingLog).TotalMilliseconds >= 2000)
                    {
                        _lastLootPendingLog = now;
                        Log($"AutoChooser: loot pending {(now - _lootPendingStart).TotalSeconds:0}s: panelGone={(panelGoneLongEnough ? "ok" : "waiting")}, lootVisible={lootAvailable}, monstersNearby={MonstersNearby(now)}");
                    }

                    if (lootAvailable)
                    {
                        _lootPending = false;
                        _lootPhaseActive = true;
                        _lootPhaseStart = now;
                        _lastLootClick = DateTime.MinValue;
                        _lastLootItemSeen = now;
                        _lootHoverFailures.Clear();
                        Log("AutoChooser: loot on the ground, panel gone - loot pickup started.");
                    }

                    return;
                }
            }

            if (!panelVisible)
            {
                if (_panelActive && Settings.LootPickupEnabled.Value)
                {
                    _lootPending = true;
                    _lootPendingStart = now;
                    _lootPanelGoneSince = now;
                    _lootPanelBackSince = DateTime.MinValue;
                    _lootAvailCache = false;
                    _lastLootAvailCheck = DateTime.MinValue;
                    _lootAreaHash = GameController?.Area?.CurrentArea?.Hash ?? 0;
                    _lootAnchor = GameController?.Player?.GridPosNum;
                    Log("AutoChooser: panel closed, waiting for the encounter to end before looting.");
                }

                _panelActive = false;
                _votedThisRound = false;
                _gauntletBanking = false;
                _gauntletBankClicks = 0;
                _lastHandle = DateTime.MinValue;
                _followerWaitStart = DateTime.MinValue;

                // With no main panel up, the pre-encounter screen may be
                // waiting on its BEGIN button. Checked last so it can never
                // interfere with an encounter that is already running.
                TryStartUltimatum(now);
                return;
            }

            // Edge-detect the open: the first frame the panel becomes visible we just
            // mark it and wait a short settle delay so the UI is fully interactive.
            if (!_panelActive)
            {
                _panelActive = true;
                _panelOpenTime = now;

                // The encounter is under way, so the start screen's pick is
                // spent. Clearing it here (rather than only on a new altar
                // address) means a second ultimatum at the same altar still
                // gets a fresh choice.
                _startCardPicked = false;
                return;
            }

            if ((now - _panelOpenTime).TotalMilliseconds < Settings.SettleDelayMs.Value)
            {
                return;
            }

            // Safe AFK: do not move the real cursor or click while you are using
            // another window. The game only accepts input when it is foreground,
            // so acting here would just hijack the window on top.
            if (Settings.OnlyWhenGameFocused.Value)
            {
                var window = GameController?.Window;
                if (window == null || !window.IsForeground())
                {
                    return;
                }
            }

            // Act on a throttle. We keep acting while the panel is open so that, in a
            // party where Confirm stays disabled until everyone has voted, we keep
            // re-clicking Confirm until it becomes enabled and the panel closes.
            if ((now - _lastHandle).TotalMilliseconds >= Settings.RetryIntervalMs.Value)
            {
                _lastHandle = now;
                try
                {
                    HandlePanel(panel);
                }
                catch (Exception ex)
                {
                    Log($"AutoChooser: handle failed: {ex.Message}");
                }
            }
        }

        private void HandlePanel(UltimatumPanel panel)
        {
            var modifierNames = ReadModifierNames(panel);

            var choices = new List<Element>(3);
            var choicesObj = panel.ChoicesPanel?.ChoiceElements;
            if (choicesObj is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is Element el)
                    {
                        choices.Add(el);
                    }
                }
            }

            // Grueling Gauntlet: the game picks the modifier itself, so there is
            // nothing to vote on - just accept each trial. The one exception is
            // Drought (flasks gain no charges): with it active the run is not
            // worth continuing, so we bank what we have instead.
            if (Settings.GruelingGauntlet.Value)
            {
                HandleGauntletPanel(panel, choices, modifierNames);
                return;
            }

            if (choices.Count == 0)
            {
                // No choice cards — may be a "Begin" / "Next wave" screen with just a confirm button.
                if (panel.ConfirmButton is Element confirm2 && confirm2.IsValid && confirm2.IsVisible)
                {
                    ClickElement(confirm2, "confirm/begin");
                    Log("AutoChooser: no choices visible, pressed confirm/begin.");
                }

                return;
            }

            int pickIndex = -1;
            Element pick = null;
            int pickPriority = int.MaxValue;

            if (Settings.PartyLeader.Value)
            {
                (pickIndex, pick, pickPriority) = PickByPriority(choices, modifierNames);
            }
            else
            {
                if (!IsInParty())
                {
                    Log("AutoChooser: not in a party, voting by own priority.");

                    (pickIndex, pick, pickPriority) = PickByPriority(choices, modifierNames);
                }
                else
                {
                    int leadIdx = FindLeadingVoteIndex(choices);
                    if (leadIdx >= 0)
                    {
                        pickIndex = leadIdx;
                        pick = choices[leadIdx];
                        pickPriority = -1;
                        int count = GetVoteCount(choices[leadIdx]);
                        Log($"AutoChooser: following leading vote -> option[{pickIndex}] (count {count}).");
                    }
                    else
                    {
                        if (_followerWaitStart == DateTime.MinValue)
                        {
                            _followerWaitStart = DateTime.UtcNow;
                        }

                        if ((DateTime.UtcNow - _followerWaitStart).TotalMilliseconds >= FollowerTimeoutMs)
                        {
                            Log("AutoChooser: no votes detected in time, falling back to own priority.");

                            (pickIndex, pick, pickPriority) = PickByPriority(choices, modifierNames);
                        }
                        else
                        {
                            Log($"AutoChooser: follower waiting for party votes ({(int)(DateTime.UtcNow - _followerWaitStart).TotalMilliseconds} ms).");

                            return;
                        }
                    }
                }
            }

            if (pick == null)
            {
                Log("AutoChooser: no selectable option (all set to never, or none visible); not clicking.");
                _followerWaitStart = DateTime.MinValue;
                return;
            }

            // Cast our vote. SelectedChoice is sanity-checked: some ExileApi builds
            // return garbage for UltimatumPanel.SelectedChoice, so we only trust it when
            // it's in a plausible range.
            int sel = panel.SelectedChoice;
            bool selValid = sel >= -1 && sel < choices.Count;
            bool needSelect = !_votedThisRound || (selValid && sel != pickIndex);
            if (needSelect)
            {
                ClickElement(pick, $"option[{pickIndex}]");
                if (DateTime.UtcNow < _pauseUntil) { _votedThisRound = false; return; }
                if (selValid && panel.SelectedChoice != pickIndex)
                {
                    Log($"AutoChooser: option not selected yet (SelectedChoice={panel.SelectedChoice}, want {pickIndex}), retry");

                    Thread.Sleep(90);
                    CheckPauseHotkey();
                    if (DateTime.UtcNow < _pauseUntil) { _votedThisRound = false; return; }
                    ClickElement(pick, $"option[{pickIndex}] retry");
                }
                else if (!selValid)
                {
                    // Can't verify the selection -> nudge with an extra click so a single
                    // missed click doesn't stall the round.
                    Thread.Sleep(90);
                    CheckPauseHotkey();
                    if (DateTime.UtcNow < _pauseUntil) { _votedThisRound = false; return; }
                    ClickElement(pick, $"option[{pickIndex}] retry");
                }

                string pickedName = pickIndex < modifierNames.Count && !string.IsNullOrWhiteSpace(modifierNames[pickIndex])
                    ? modifierNames[pickIndex]
                    : GetElementModifierText(pick);
                Log($"AutoChooser: selected option[{pickIndex}] '{pickedName}' (priority {pickPriority}).");
                _votedThisRound = true;
                Thread.Sleep(Settings.ClickDelayMs.Value);
            }

            // Click Confirm on every pass. In a party it stays disabled until everyone
            // has voted, so the click is a no-op until then and succeeds once enabled
            // (the panel then closes and our per-round state resets).
            if (panel.ConfirmButton is Element confirm && confirm.IsValid && confirm.IsVisible)
            {
                ClickElement(confirm, "confirm/start");
                Log("AutoChooser: pressed start/confirm.");
            }
            else
            {
                Log("AutoChooser: confirm/start button not found or not visible.");
            }
        }

        // --- Grueling Gauntlet -----------------------------------------------
        // With "modifiers are chosen for you" there is nothing to vote on, so
        // the only decision left each round is accept-or-bank. Which modifiers
        // end the run is up to the user: the checkboxes next to the priority
        // sliders mark them (Drought is ticked by default - flasks gain no
        // charges, so the run is not worth continuing).

        // Latched for as long as the panel stays up: once we have decided to
        // bank, this round is over for us no matter what the panel shows next.
        private bool _gauntletBanking;
        private int _gauntletBankClicks;
        private const int GauntletMaxBankClicks = 4;

        // A modifier stops the run when its checkbox is ticked. Matching reuses
        // MatchBaseMod, so tiered names resolve to their own entry ("Quicksand
        // III" to Quicksand III, not to Quicksand).
        private bool IsStopperName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            int idx = MatchBaseMod(Normalize(name));
            return Settings.IsGauntletStopper(idx);
        }

        private void HandleGauntletPanel(UltimatumPanel panel, List<Element> choices, List<string> modifierNames)
        {
            // Already decided to bank on an earlier pass of this same panel.
            // Keep trying "take rewards" and never fall through to accept -
            // after the click the panel briefly shows no cards, and pressing
            // confirm there would start the very wave we are trying to avoid.
            if (_gauntletBanking)
            {
                if (!ClickTakeRewards(panel))
                {
                    _gauntletBanking = false;
                }

                return;
            }

            // No cards on screen: the opening "Begin" screen, or the round
            // header between waves. Either way just press the button.
            if (choices.Count == 0)
            {
                if (panel.ConfirmButton is Element begin && begin.IsValid && begin.IsVisible)
                {
                    ClickElement(begin, "gauntlet confirm/begin");
                    Log("AutoChooser: gauntlet - no cards on screen, pressed confirm/begin.");
                }
                else
                {
                    Log("AutoChooser: gauntlet - no cards and no confirm button visible.");
                }

                return;
            }

            int selected = FindSelectedChoiceIndex(panel, choices);
            bool bank;

            if (selected >= 0)
            {
                string name = GetChoiceName(choices, modifierNames, selected);
                bank = IsStopperName(name);
                Log($"AutoChooser: gauntlet - game picked option[{selected}] '{name}'{(bank ? " -> marked as stopper, banking rewards" : "")}.");
            }
            else
            {
                // The selection could not be read. Accepting a stopper round by
                // mistake can cost the whole run; banking by mistake only costs
                // the rounds we would have won after this one. So fall back to
                // the cautious answer: bank if a stopper is on screen at all.
                bank = false;
                string hit = null;
                for (int i = 0; i < choices.Count && !bank; i++)
                {
                    string name = GetChoiceName(choices, modifierNames, i);
                    if (IsStopperName(name))
                    {
                        bank = true;
                        hit = name;
                    }
                }

                Log($"AutoChooser: gauntlet - could not tell which card is selected; " +
                    $"{(bank ? $"'{hit}' is among them, banking rewards" : "no stopper on screen, accepting")}.");
            }

            if (!bank)
            {
                if (panel.ConfirmButton is Element accept && accept.IsValid && accept.IsVisible)
                {
                    ClickElement(accept, "gauntlet accept trial");
                    Log("AutoChooser: gauntlet - pressed accept trial.");
                }
                else
                {
                    Log("AutoChooser: gauntlet - accept trial button not found or not visible.");
                }

                return;
            }

            _gauntletBanking = true;
            if (!ClickTakeRewards(panel))
            {
                // The click never landed (pause hotkey, or the button vanished).
                // Do not stay latched: the user may have taken over and chosen
                // to continue, and a latched flag would keep pressing "take
                // rewards" over their decision on every later round.
                _gauntletBanking = false;
            }
        }

        // Re-clicking is capped: unlike confirm (a no-op until everyone has
        // voted), a stray click here could land on the rewards inventory that
        // opens once the encounter ends. Returns true when a click was fired.
        private bool ClickTakeRewards(UltimatumPanel panel)
        {
            if (_gauntletBankClicks >= GauntletMaxBankClicks)
            {
                Log("AutoChooser: gauntlet - take rewards already clicked, waiting for the panel to close.");
                return true;
            }

            var take = FindTakeRewardsButton(panel);
            if (take == null)
            {
                Log("AutoChooser: gauntlet - take rewards button not found; not clicking anything.");
                return false;
            }

            if (!ClickElement(take, "gauntlet take rewards"))
            {
                Log("AutoChooser: gauntlet - take rewards click was interrupted.");
                return false;
            }

            _gauntletBankClicks++;
            Log($"AutoChooser: gauntlet - pressed take rewards ({_gauntletBankClicks}/{GauntletMaxBankClicks}).");
            return true;
        }

        // Which of the three cards the game marked as chosen. -1 when unknown.
        private int FindSelectedChoiceIndex(UltimatumPanel panel, List<Element> choices)
        {
            // Strongly typed first: each card knows whether it is the selected one.
            try
            {
                var typed = panel.ChoicesPanel?.ChoiceElements;
                if (typed != null)
                {
                    for (int i = 0; i < typed.Count; i++)
                    {
                        var c = typed[i];
                        if (c != null && c.IsSelectedChoice)
                        {
                            return i;
                        }
                    }
                }
            }
            catch { }

            // Then the index - trusted only in a plausible range, since some
            // builds return garbage for SelectedChoice.
            try
            {
                int sel = panel.SelectedChoice;
                if (sel >= 0 && sel < choices.Count)
                {
                    return sel;
                }
            }
            catch { }

            return -1;
        }

        private string GetChoiceName(List<Element> choices, List<string> modifierNames, int index)
        {
            if (index < 0) return string.Empty;

            if (index < modifierNames.Count && !string.IsNullOrWhiteSpace(modifierNames[index]))
            {
                return modifierNames[index];
            }

            return index < choices.Count ? GetElementModifierText(choices[index]) : string.Empty;
        }

        // "TAKE REWARDS" has no strongly-typed accessor - ExileApi only exposes
        // ConfirmButton, which is ACCEPT TRIAL - so it is located by its label.
        private Element FindTakeRewardsButton(UltimatumPanel panel)
        {
            return FindDescendantByText(panel, "take reward", 0);
        }

        private Element FindDescendantByText(Element el, string needle, int depth)
        {
            if (el == null || depth > 6) return null;

            long kids = 0;
            try
            {
                if (!el.IsValid) return null;
                kids = el.ChildCount;

                string text = SafeText(el, 60);
                if (text.Length > 0 && text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var r = el.GetClientRect();
                    if (r.Width > 0 && r.Height > 0) return el;
                }
            }
            catch { return null; }

            for (int i = 0; i < kids; i++)
            {
                Element child = null;
                try { child = el.GetChildAtIndex(i); } catch { continue; }
                if (child == null) continue;

                var hit = FindDescendantByText(child, needle, depth + 1);
                if (hit != null) return hit;
            }

            return null;
        }

        private (int Index, Element Element, int Priority) PickByPriority(List<Element> choices, List<string> modifierNames)
        {
            int bestIndex = -1;
            int bestPriority = int.MaxValue;
            Element best = null;

            // Fallback when everything is set to "never" (100): remember the
            // least-bad option so ForcePick can still choose something.
            int anyIndex = -1;
            int anyPriority = int.MaxValue;
            Element any = null;

            for (int i = 0; i < choices.Count; i++)
            {
                var el = choices[i];
                if (el == null || !el.IsValid || !el.IsVisible)
                {
                    continue;
                }

                var rect = el.GetClientRect();
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    continue;
                }

                string name;
                if (i < modifierNames.Count && !string.IsNullOrWhiteSpace(modifierNames[i]))
                {
                    name = modifierNames[i];
                }
                else
                {
                    name = GetElementModifierText(el);
                }

                int priority = GetPriority(name);

                Log($"AutoChooser: option[{i}] '{name}' priority={priority}");

                if (priority < anyPriority)
                {
                    anyPriority = priority;
                    anyIndex = i;
                    any = el;
                }

                // Priority 100 means "never take" -> skip this card.
                if (priority >= 100)
                {
                    continue;
                }

                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    bestIndex = i;
                    best = el;
                }
            }

            if (best == null && Settings.ForcePickWhenAllAvoided.Value && any != null)
            {
                best = any;
                bestIndex = anyIndex;
                bestPriority = anyPriority;
            }

            return (bestIndex, best, bestPriority);
        }

        // Reads a modifier name from a choice card when panel.Modifiers is empty (some
        // ExileApi builds no longer populate it). We walk the card's element tree and
        // return the first descendant whose visible text contains a known base mod name.
        private string GetElementModifierText(Element el)
        {
            // Try Element.Text property first (works in some ExileApi builds where GetText() fails).
            try
            {
                var textProp = el?.GetType().GetProperty("Text");
                if (textProp != null)
                {
                    string text = textProp.GetValue(el) as string;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string normalized = Normalize(text);
                        if (MatchBaseMod(normalized) >= 0)
                        {
                            return normalized;
                        }
                    }
                }
            }
            catch { }

            // Try Element.TextNoTags property.
            try
            {
                var textNoTagsProp = el?.GetType().GetProperty("TextNoTags");
                if (textNoTagsProp != null)
                {
                    string text = textNoTagsProp.GetValue(el) as string;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        string normalized = Normalize(text);
                        if (MatchBaseMod(normalized) >= 0)
                        {
                            return normalized;
                        }
                    }
                }
            }
            catch { }

            // Walk children using Text property.
            string match = FindFirstMatchingChildText(el, 6);
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }

            return Normalize(el.GetText(4096) ?? string.Empty);
        }

        private string FindFirstMatchingChildText(Element el, int depth)
        {
            if (el == null || depth < 0)
            {
                return null;
            }

            // Try Text property first.
            try
            {
                var textProp = el.GetType().GetProperty("Text");
                if (textProp != null)
                {
                    string t = Normalize(textProp.GetValue(el) as string ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(t) && MatchBaseMod(t) >= 0)
                    {
                        return t;
                    }
                }
            }
            catch { }

            // Fallback to GetText().
            string gt = Normalize(el.GetText(1024) ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(gt) && MatchBaseMod(gt) >= 0)
            {
                return gt;
            }

            var children = el.Children;
            if (children == null)
            {
                return null;
            }

            foreach (var c in children)
            {
                if (c is Element ce)
                {
                    var r = FindFirstMatchingChildText(ce, depth - 1);
                    if (!string.IsNullOrWhiteSpace(r))
                    {
                        return r;
                    }
                }
            }

            return null;
        }

        private int FindLeadingVoteIndex(List<Element> choices)
        {
            int bestIdx = -1;
            int bestCount = 0;

            for (int i = 0; i < choices.Count; i++)
            {
                var el = choices[i];
                if (el == null || !el.IsValid || !el.IsVisible)
                {
                    continue;
                }

                int count = GetVoteCount(el);
                if (count > bestCount)
                {
                    bestCount = count;
                    bestIdx = i;
                }
            }

            return bestIdx;
        }

        private static int GetVoteCount(Element el)
        {
            int max = 0;
            CollectPureInt(el, ref max, 5);
            return max;
        }

        private static void CollectPureInt(Element el, ref int max, int depth)
        {
            if (el == null || depth < 0)
            {
                return;
            }

            string t = (el.GetText(32) ?? string.Empty).Trim();
            if (t.Length > 0 && t.Length <= 6 && Regex.IsMatch(t, @"^\d+(/\d+)?$"))
            {
                int v = 0;
                foreach (char c in t)
                {
                    if (char.IsDigit(c))
                    {
                        v = v * 10 + (c - '0');
                    }
                    else
                    {
                        break;
                    }
                }

                if (v > max)
                {
                    max = v;
                }
            }

            var children = el.Children;
            if (children == null)
            {
                return;
            }

            foreach (var c in children)
            {
                if (c is Element ce)
                {
                    CollectPureInt(ce, ref max, depth - 1);
                }
            }
        }

        private bool IsInParty()
        {
            try
            {
                var ingame = GameController?.IngameState;
                if (ingame == null)
                {
                    return false;
                }

                var sdType = ingame.GetType();
                object sd = sdType.GetProperty("ServerData")?.GetValue(ingame)
                         ?? sdType.GetProperty("Data")?.GetValue(ingame);
                if (sd == null)
                {
                    return false;
                }

                var sdProps = sd.GetType();

                // Primary: your own party status ("None" => not in a party).
                var status = sdProps.GetProperty("PartyStatusType")?.GetValue(sd);
                if (status != null)
                {
                    string statusName = Enum.GetName(status.GetType(), status);
                    if (!string.IsNullOrEmpty(statusName) && statusName != "None")
                    {
                        Log($"AutoChooser: in party detected (status {statusName}).");

                        return true;
                    }
                }

                // Fallback: count party members (>= 2 means other members are present).
                var members = sdProps.GetProperty("PartyMembers")?.GetValue(sd) as IEnumerable;
                if (members != null)
                {
                    int n = 0;
                    foreach (var m in members)
                    {
                        n++;
                        if (n >= 2)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"AutoChooser: party check failed: {ex.Message}");
            }

            return false;
        }

        private static List<string> ReadModifierNames(UltimatumPanel panel)
        {
            // panel.Modifiers is broken in some ExileApi builds; ChoicesPanel.Modifiers works.
            var source = panel.ChoicesPanel?.Modifiers ?? panel.Modifiers;
            if (source == null)
            {
                return new List<string>(3);
            }

            var names = new List<string>(3);
            if (source is IEnumerable mods)
            {
                foreach (var m in mods)
                {
                    if (m == null)
                    {
                        names.Add(string.Empty);
                        continue;
                    }

                    string name = null;
                    var nameProp = m.GetType().GetProperty("Name");
                    if (nameProp != null)
                        name = nameProp.GetValue(m) as string;

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        var descProp = m.GetType().GetProperty("Description");
                        if (descProp != null)
                            name = descProp.GetValue(m) as string;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        var idProp = m.GetType().GetProperty("Id");
                        if (idProp != null)
                            name = idProp.GetValue(m) as string;
                    }

                    if (string.IsNullOrWhiteSpace(name))
                        name = m.ToString();

                    names.Add(Normalize(name ?? string.Empty));
                }
            }

            return names;
        }


        // Priority for a modifier that is not in the list. Fixed in code: the
        // list covers every known modifier, so this only applies to something
        // the game has added since - and 20 ("take it only if nothing better is
        // offered") is the sane answer for an unknown.
        private const int UnknownModifierPriority = 20;

        private int GetPriority(string modifierName)
        {
            if (string.IsNullOrWhiteSpace(modifierName))
            {
                return UnknownModifierPriority;
            }

            string norm = Normalize(modifierName);
            int idx = MatchBaseMod(norm);
            var priorities = Settings.Priorities;
            if (idx >= 0 && priorities != null && idx < priorities.Count &&
                int.TryParse(priorities[idx], out int p))
            {
                return p;
            }

            return UnknownModifierPriority;
        }

        private static int MatchBaseMod(string norm)
        {
            if (string.IsNullOrEmpty(norm))
            {
                return -1;
            }

            int bestIdx = -1;
            int bestLen = 0;

            for (int i = 0; i < AutoChooserSettings.UltimatumMods.Length; i++)
            {
                string baseName = Normalize(AutoChooserSettings.UltimatumMods[i]);
                if (baseName.Length == 0)
                {
                    continue;
                }

                if (baseName.Length > bestLen && norm.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    bestLen = baseName.Length;
                    bestIdx = i;
                }
            }

            return bestIdx;
        }

        // Throttled wrapper around HasNearbyHostileMonsters - the entity scan is not
        // free, so we cache the result for a short interval instead of running it
        // on every rendered frame.
        private bool MonstersNearby(DateTime now)
        {
            if ((now - _lastMonsterCheck).TotalMilliseconds >= MonsterCheckIntervalMs)
            {
                _lastMonsterCheck = now;
                _monstersNearbyCache = HasNearbyHostileMonsters();
            }

            return _monstersNearbyCache;
        }

        // The old 200-unit radius covered most of the arena, so in a live map stray
        // monsters outside the ultimatum circle kept this true forever and no loot
        // click ever fired. Now it only looks for hostiles that are actually close
        // to the player (LootMonsterDistance, default 40 units).
        private bool HasNearbyHostileMonsters()
        {
            float maxDist = LootMonsterDistance;
            try
            {
                var entities = GameController?.EntityListWrapper?.OnlyValidEntities;
                if (entities == null) return false;

                foreach (var entity in entities)
                {
                    if (entity == null || !entity.IsValid) continue;
                    if (entity.Type != EntityType.Monster) continue;
                    if (!entity.IsAlive || !entity.IsHostile) continue;

                    float dist = entity.DistancePlayer;
                    if (dist > 0f && dist < maxDist)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        // Unlike HasNearbyHostileMonsters this ignores distance and only counts the
        // encounter's own spawns (Metadata/Monsters/LeagueUltimatum/...), so it
        // answers "is a round still running" rather than "is something next to me".
        private bool HasLiveUltimatumMonsters()
        {
            try
            {
                var entities = GameController?.EntityListWrapper?.OnlyValidEntities;
                if (entities == null) return false;

                foreach (var entity in entities)
                {
                    if (entity == null || !entity.IsValid) continue;
                    if (entity.Type != EntityType.Monster) continue;
                    if (!entity.IsAlive || !entity.IsHostile) continue;

                    string path = entity.Path;
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("LeagueUltimatum", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        // Debug helper: which hostile is currently blocking loot clicks.
        private string DescribeNearestHostile()
        {
            try
            {
                var entities = GameController?.EntityListWrapper?.OnlyValidEntities;
                if (entities == null) return "entity scan unavailable";

                string bestName = null;
                float bestDist = float.MaxValue;
                foreach (var entity in entities)
                {
                    if (entity == null || !entity.IsValid) continue;
                    if (entity.Type != EntityType.Monster) continue;
                    if (!entity.IsAlive || !entity.IsHostile) continue;

                    float dist = entity.DistancePlayer;
                    if (dist > 0f && dist < bestDist)
                    {
                        bestDist = dist;
                        bestName = entity.RenderName;
                        if (string.IsNullOrWhiteSpace(bestName)) bestName = entity.Path;
                    }
                }

                return bestName != null
                    ? $"hostile '{bestName}' at {bestDist:0}u (gate {LootMonsterDistance}u)"
                    : "no hostiles found";
            }
            catch (Exception ex)
            {
                return $"hostile scan failed: {ex.Message}";
            }
        }

        // True when the player has moved to a different area since the loot
        // waiting was armed - loot automation must not follow them there.
        private bool LootAreaChanged()
        {
            try
            {
                uint hash = GameController?.Area?.CurrentArea?.Hash ?? 0;
                return _lootAreaHash != 0 && hash != 0 && hash != _lootAreaHash;
            }
            catch
            {
                return false;
            }
        }

        // True when the player walked too far from the spot where loot waiting
        // was armed (the ultimatum arena). The arena is tiny, so someone who
        // walked away is mapping - loot automation must not follow them.
        private bool LeftLootAnchor()
        {
            try
            {
                if (_lootAnchor == null) return false;
                var player = GameController?.Player;
                if (player == null || !player.IsValid) return false;

                var pos = player.GridPosNum;
                float dx = pos.X - _lootAnchor.Value.X;
                float dy = pos.Y - _lootAnchor.Value.Y;
                float max = LootMaxWalkDistance;
                return dx * dx + dy * dy > max * max;
            }
            catch
            {
                return false;
            }
        }

        // The loot phase ended while the panel is still gone (timeout or a quiet
        // gap between drops): go back to waiting so late/leftover loot still gets
        // picked. The 5-minute cap and the panel-gone timer restart from here.
        private void RearmLootPending()
        {
            if (!Settings.LootPickupEnabled.Value)
            {
                return;
            }

            _lootPending = true;
            _lootPendingStart = DateTime.UtcNow;
            _lootAvailCache = false;
            _lastLootAvailCheck = DateTime.MinValue;
        }

        // Scans the visible ground labels and returns the nearest click-ready one:
        // within max distance, on-screen (not edge-clamped), pickable (party
        // allocation) and not hover-blacklisted. Null when none. Also reports how
        // many labels are visible and how many passed the distance/edge filters.
        private ItemsOnGroundLabelElement.VisibleGroundItemDescription FindBestLootLabel(
            out RectangleF bestRect, out float bestDist, out int labelCount, out int inRangeCount)
        {
            bestRect = default;
            bestDist = float.MaxValue;
            labelCount = 0;
            inRangeCount = 0;

            var groundElement = GameController?.IngameState?.IngameUi?.ItemsOnGroundLabelElement;
            var labels = groundElement?.VisibleGroundItemLabels;
            if (labels == null || labels.Count == 0)
            {
                return null;
            }

            labelCount = labels.Count;

            var window = GameController?.Window;
            if (window == null)
            {
                return null;
            }

            RectangleF windowRect = window.GetWindowRectangleTimeCache;

            // Party-allocation lookup: LabelsOnGroundVisible carries CanPickUp,
            // VisibleGroundItemLabels does not. Keyed by label element address.
            var canPickUpByLabel = new Dictionary<long, bool>(labelCount);
            var allocLabels = groundElement.LabelsOnGroundVisible;
            if (allocLabels != null)
            {
                for (int i = 0; i < allocLabels.Count; i++)
                {
                    var l = allocLabels[i];
                    if (l?.Label != null)
                    {
                        canPickUpByLabel[l.Label.Address] = l.CanPickUp;
                    }
                }
            }

            int maxDist = LootPickupMaxDistance;
            ItemsOnGroundLabelElement.VisibleGroundItemDescription best = null;

            for (int i = 0; i < labels.Count; i++)
            {
                var cand = labels[i];
                var ent = cand?.Entity;
                if (ent == null || !ent.IsValid) continue;

                // Gold collects itself when you walk over it - never click it.
                string entPath = ent.Path ?? string.Empty;
                if (entPath.IndexOf("Items/Gold/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entPath.IndexOf("Items/Currency/GoldCoin", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                float dist = ent.DistancePlayer;
                if (dist > maxDist) continue;

                var lbl = cand.Label;
                if (lbl == null || !lbl.IsValid || !lbl.IsVisible) continue;

                // Fallback gold check by the visible text ("205 GOLD") for when
                // the entity path is unavailable.
                if (IsGoldText(lbl.GetText(64))) continue;

                RectangleF rect = cand.ClientRect;
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    rect = lbl.GetClientRect();
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                }

                // Ignore labels clamped to the screen edge (item is off-screen;
                // clicking there just walks the character somewhere random).
                if (!IsLabelClickableArea(rect, windowRect)) continue;

                inRangeCount++;

                // Skip labels we are not allowed to pick up (party allocation).
                if (canPickUpByLabel.TryGetValue(lbl.Address, out bool pickAllowed) && !pickAllowed) continue;

                // Skip labels the game repeatedly refuses to highlight.
                if (_lootHoverFailures.TryGetValue(lbl.Address, out int fails) && fails >= LootMaxHoverFailures) continue;

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = cand;
                    bestRect = rect;
                }
            }

            return best;
        }

        // Clicks the nearest visible ground item. Returns true only when an actual
        // click was fired (drives the "nothing left to loot" early stop): labels
        // that are not pickable or fail hover verification are skipped instead.
        //
        // Two things made the old version click "мимо":
        //  - it read labels via IngameUi.ItemsOnGroundLabelsVisible; the maintained
        //    source on this ExileCore build (PickItV2 uses it) is
        //    ItemsOnGroundLabelElement.VisibleGroundItemLabels, which returns
        //    Entity+Label+ClientRect as one consistent bundle from the game's own
        //    label layout pass.
        //  - it clicked blind a few ms after moving the cursor. The game only
        //    highlights/targets a ground item a frame or two after the cursor
        //    arrives; a click before that lands on unhighlighted ground and the
        //    character just walks there. So now we wait for Targetable.isTargeted
        //    (PickIt-style) and only then click.
        private bool TryPickupLoot()
        {
            try
            {
                var best = FindBestLootLabel(out RectangleF bestRect, out float bestDist, out int labelCount, out int inRangeCount);

                if (labelCount == 0)
                {
                    Log("AutoChooser: loot: 0 ground labels visible.");

                    return false;
                }

                if (best == null)
                {
                    Log($"AutoChooser: loot: {labelCount} labels visible, {inRangeCount} in range, none click-ready (allocated/edge/hover-blocked).");

                    return false;
                }

                Vector2 windowTopLeft = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
                Vector2 center = bestRect.Center + windowTopLeft;

                int j = Settings.ClickJitter.Value;
                float jx = j > 0 ? (float)(_rng.NextDouble() * (j * 2) - j) : 0f;
                float jy = j > 0 ? (float)(_rng.NextDouble() * (j * 2) - j) : 0f;
                Vector2 clickPos = center + new Vector2(jx, jy);

                Log($"AutoChooser: loot hover at ({clickPos.X:0},{clickPos.Y:0}) dist={bestDist:0} rect=({bestRect.X:0},{bestRect.Y:0},{bestRect.Width:0}x{bestRect.Height:0})");

                if (DateTime.UtcNow < _pauseUntil) return false;

                // Move, then WAIT until the game actually highlights the item under
                // the cursor - clicking before that is a move-here click on empty
                // ground. NB: the SharpDX Vector2 overload of Input.SetCursorPos is
                // obsolete on this ExileCore build; the Numerics one is the
                // maintained path (PickItV2 uses it).
                Input.SetCursorPos(new System.Numerics.Vector2(clickPos.X, clickPos.Y));

                long lblAddr = best.Label.Address;
                if (!WaitForLootTarget(best.Entity, best.Label, LootHoverTimeoutMs))
                {
                    _lootHoverFailures[lblAddr] = _lootHoverFailures.TryGetValue(lblAddr, out int f) ? f + 1 : 1;
                    Log($"AutoChooser: loot hover not confirmed (attempt {_lootHoverFailures[lblAddr]}/{LootMaxHoverFailures}), click skipped.");

                    return false;
                }

                NativeMouse.LeftClick();
                _lootHoverFailures.Remove(lblAddr);
                Log($"AutoChooser: loot click at ({clickPos.X:0},{clickPos.Y:0}) dist={bestDist:0} (target confirmed).");

                Thread.Sleep(10);
                return true;
            }
            catch (Exception ex)
            {
                Log($"AutoChooser: loot pickup failed: {ex.Message}");
                return false;
            }
        }

        // "205 GOLD", "22 gold" - the visible text of gold piles.
        private static bool IsGoldText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (!text.EndsWith("GOLD", StringComparison.OrdinalIgnoreCase)) return false;

            string digits = text.Substring(0, text.Length - 4).Replace(" ", string.Empty);
            return digits.Length > 0 && digits.All(char.IsDigit);
        }

        // Same rule PickIt uses: the label center must lie inside the game window
        // client area with a margin, so edge-clamped labels (off-screen items)
        // are not clicked.
        private static bool IsLabelClickableArea(RectangleF labelRect, RectangleF windowRect)
        {
            RectangleF clientWindow = windowRect with { Location = Vector2.Zero };
            clientWindow.Inflate(-LootEdgeMarginPx, -LootEdgeMarginPx);
            Vector2 c = labelRect.Center;
            return clientWindow.Contains(c.X, c.Y);
        }

        private bool WaitForLootTarget(Entity item, Element label, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            do
            {
                if (IsLootTargeted(item, label)) return true;
                Thread.Sleep(LootHoverPollMs);
                CheckPauseHotkey();
                if (DateTime.UtcNow < _pauseUntil) return false;
                if (IsGamePausedNow()) return false;
            }
            while (sw.ElapsedMilliseconds < timeoutMs);

            return IsLootTargeted(item, label);
        }

        // The game marks the entity under the cursor as targeted (and highlights
        // its label) a frame or two after the cursor arrives. This is the exact
        // signal PickIt waits for before clicking.
        private static bool IsLootTargeted(Entity item, Element label)
        {
            try
            {
                if (item == null) return false;
                var targetable = item.GetComponent<Targetable>();
                if (targetable != null)
                {
                    return targetable.isTargeted;
                }

                return label != null && label.HasShinyHighlight;
            }
            catch
            {
                return false;
            }
        }

        // Returns true only when a click was actually fired. The pause hotkey is
        // polled inside the cursor travel, so a click can be abandoned halfway;
        // callers that latch state on "we clicked" have to know the difference.
        private bool ClickElement(Element el, string label)
        {
            RectangleF rect = el.GetClientRect();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return false;
            }

            var window = GameController?.Window;
            if (window == null)
            {
                return false;
            }

            var rectCache = window.GetWindowRectangleTimeCache;
            Vector2 topLeft = rectCache.TopLeft;
            Vector2 center = rect.Center + topLeft;

            int j = Settings.ClickJitter.Value;
            int jx = j > 0 ? _rng.Next(-j, j + 1) : 0;
            int jy = j > 0 ? _rng.Next(-j, j + 1) : 0;
            int x = (int)Math.Round(center.X) + jx;
            int y = (int)Math.Round(center.Y) + jy;

            Log($"AutoChooser: click {label} at screen ({x},{y}) (winTopLeft {topLeft.X:0},{topLeft.Y:0}, center {center.X:0},{center.Y:0})");

            try
            {
                MoveMouseSmooth(x, y);
                if (DateTime.UtcNow < _pauseUntil) return false;
                if (IsGamePausedNow()) return false;
                Thread.Sleep(20 + _rng.Next(0, 40));
                NativeMouse.LeftClick();
                return true;
            }
            catch (Exception ex)
            {
                Log($"AutoChooser: click failed: {ex.Message}");
                return false;
            }
        }

        private void MoveMouseSmooth(int targetX, int targetY)
        {
            NativeMouse.GetCursorPos(out int sx, out int sy);

            if (!Settings.SmoothMouse.Value)
            {
                NativeMouse.SetCursorPos(targetX, targetY);
                return;
            }

            int dx = targetX - sx;
            int dy = targetY - sy;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            // Duration scales with distance, otherwise a far move in a fixed time
            // looks like an instant teleport while a near move looks smooth.
            int duration = (int)(dist * 1.2) + Settings.MouseSpeedMs.Value;
            duration = Math.Min(duration, 1200);

            int steps = Math.Max(2, duration / 10);
            double perp = _rng.NextDouble() * 0.12 + 0.04;
            int arc = (int)(dist * perp) * (_rng.Next(0, 2) == 0 ? -1 : 1);

            for (int s = 1; s <= steps; s++)
            {
                CheckPauseHotkey();
                if (DateTime.UtcNow < _pauseUntil) return;

                // Esc can be pressed mid-travel; abandon the move rather than
                // keep dragging the cursor across a frozen game.
                if (IsGamePausedNow()) return;

                double t = (double)s / steps;
                double e = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
                int x = sx + (int)Math.Round(dx * e);
                int y = sy + (int)Math.Round(dy * e + arc * Math.Sin(Math.PI * t));
                NativeMouse.SetCursorPos(x, y);
                Thread.Sleep(duration / steps);
            }

            NativeMouse.SetCursorPos(targetX, targetY);
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
            while (normalized.Contains("  ", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
            }

            return normalized;
        }

        private static class NativeMouse
        {
            [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
            private static extern bool SetCursorPosNative(int x, int y);

            [DllImport("user32.dll")]
            private static extern bool GetCursorPos(out POINT lpPoint);

            [DllImport("user32.dll")]
            private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

            [StructLayout(LayoutKind.Sequential)]
            private struct POINT
            {
                public int X;
                public int Y;
            }

            private const int MouseEventLeftDown = 0x02;
            private const int MouseEventLeftUp = 0x04;

            public static void GetCursorPos(out int x, out int y)
            {
                POINT p;
                GetCursorPos(out p);
                x = p.X;
                y = p.Y;
            }

            public static void SetCursorPos(int x, int y)
            {
                SetCursorPosNative(x, y);
            }

            public static void LeftClick()
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, 0);
                Thread.Sleep(12);
                mouse_event(MouseEventLeftUp, 0, 0, 0, 0);
            }
        }
    }

    public class AutoChooserSettings : ISettings
    {
        internal static readonly string[] UltimatumMods =
        {
            "Ailment and Curse Reflection",
            "Blistering Cold", "Blistering Cold II", "Blistering Cold III", "Blistering Cold IV",
            "Blood Altar",
            "Buffs Expire Faster",
            "Choking Miasma", "Choking Miasma II", "Choking Miasma III", "Choking Miasma IV",
            "Deadly Monsters",
            "Dexterous Monsters",
            "Drought",
            "Escalating Damage Taken",
            "Escalating Monster Speed",
            "Hindering Flasks",
            "Impenetrable Monsters",
            "Impurity",
            "Lethal Rare Monsters",
            "Less Cooldown Recovery",
            "Lessened Reach",
            "Lightning Damage from Mana Costs",
            "Limited Arena",
            "Occasional Impotence",
            "Overwhelming Monsters",
            "Precise Monsters",
            "Prismatic Monsters",
            "Profane Monsters",
            "Putrid Monsters",
            "Quicksand", "Quicksand II", "Quicksand III", "Quicksand IV",
            "Raging Dead", "Raging Dead II", "Raging Dead III", "Raging Dead IV",
            "Random Projectiles",
            "Razor Dance", "Razor Dance II", "Razor Dance III", "Razor Dance IV",
            "Reduced Recovery",
            "Resistant Monsters",
            "Restless Ground", "Restless Ground II", "Restless Ground III", "Restless Ground IV",
            "Ruin",
            "Shattered Shield",
            "Shielding Monsters",
            "Siphoned Charges",
            "Siphoning Monsters",
            "Stalking Ruin", "Stalking Ruin II", "Stalking Ruin III", "Stalking Ruin IV",
            "Stormcaller Runes", "Stormcaller Runes II", "Stormcaller Runes III", "Stormcaller Runes IV",
            "The Trialmaster",
            "Totem of Costly Might",
            "Totem of Costly Potency",
            "Treacherous Auras",
            "Unlucky Criticals",
            "Unstoppable Monsters",
            "Waning Spirit"
        };

        public AutoChooserSettings()
        {
            Priorities = new List<string>(DefaultPriorities);
            GauntletStop = new List<bool>(DefaultGauntletStop);
            OptionPriorityPanel.DrawDelegate = DrawOptionPriorities;
        }

        // Only Drought is a stopper out of the box - it is the one modifier that
        // makes a run not worth continuing. Built from the names rather than
        // written as a literal so the flags cannot drift out of alignment when
        // UltimatumMods is edited.
        private static readonly bool[] DefaultGauntletStop = BuildDefaultGauntletStop();

        private static bool[] BuildDefaultGauntletStop()
        {
            var flags = new bool[UltimatumMods.Length];
            for (int i = 0; i < UltimatumMods.Length; i++)
            {
                flags[i] = UltimatumMods[i].IndexOf("Drought", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return flags;
        }

        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        [Menu("This client is the party leader (picks the modifier). Uncheck to follow the party leader's vote", 0)]
        public ToggleNode PartyLeader { get; set; } = new ToggleNode(true);

        [Menu("Grueling Gauntlet: modifiers are chosen by the game - just press Accept Trial (banks the rewards instead if Drought is the chosen modifier)", 14)]
        public ToggleNode GruelingGauntlet { get; set; } = new ToggleNode(false);

        [Menu("Auto-start: press BEGIN on the pre-encounter screen to start the ultimatum", 15)]
        public ToggleNode AutoStart { get; set; } = new ToggleNode(true);

        [Menu("Hotkey to pause the bot for the duration set below", 11)]
        public HotkeyNodeV2 PauseHotkey { get; set; } = new HotkeyNodeV2(Keys.F);

        [Menu("Pause duration after the hotkey press (ms)", 12)]
        public RangeNode<int> PauseDurationMs { get; set; } = new RangeNode<int>(6000, 500, 60000);

        [Menu("Only act when the game window is in the foreground (safe AFK)", 1)]
        public ToggleNode OnlyWhenGameFocused { get; set; } = new ToggleNode(true);

        [Menu("If all 3 present options are set to 100 (never), pick least-bad anyway", 2)]
        public ToggleNode ForcePickWhenAllAvoided { get; set; } = new ToggleNode(true);

        [Menu("Delay between option and start click (ms)", 4)]
        public RangeNode<int> ClickDelayMs { get; set; } = new RangeNode<int>(300, 0, 5000);

        [Menu("Wait after panel opens before clicking (ms)", 5)]
        public RangeNode<int> SettleDelayMs { get; set; } = new RangeNode<int>(250, 0, 2000);

        [Menu("Retry interval while panel stays open (ms)", 6)]
        public RangeNode<int> RetryIntervalMs { get; set; } = new RangeNode<int>(1500, 200, 10000);

        [Menu("Smooth (human-like) mouse movement", 7)]
        public ToggleNode SmoothMouse { get; set; } = new ToggleNode(true);

        [Menu("Min mouse move duration (ms); far moves take longer", 8)]
        public RangeNode<int> MouseSpeedMs { get; set; } = new RangeNode<int>(140, 20, 800);

        [Menu("Random click offset (px) for human feel", 9)]
        public RangeNode<int> ClickJitter { get; set; } = new RangeNode<int>(4, 0, 25);

        [Menu("Loot pickup after the encounter ends", 13)]
        public ToggleNode LootPickupEnabled { get; set; } = new ToggleNode(true);

        [Menu("Debug logging", 10)]
        public ToggleNode Debug { get; set; } = new ToggleNode(false);

        [JsonIgnore]
        [Menu("Ultimatum option priorities (1 = always, >= Avoid threshold = never)", 6)]
        public CustomNode OptionPriorityPanel { get; } = new CustomNode();

        // Persisted, but never drawn by the settings reflection pass: the
        // priorities are rendered by hand in DrawOptionPriorities above.
        // Without [IgnoreMenu] ExileCore walks this property looking for a node
        // type it can draw, finds a plain List<string> and logs
        // "... is not a supported settings element. This is probably a bug in
        // the plugin." on every load. The value itself always saved fine.
        [IgnoreMenu]
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> Priorities { get; set; }

        // Per-modifier "end the run" flags for Grueling Gauntlet, index-aligned
        // with UltimatumMods. Same deal as Priorities: persisted, drawn by hand,
        // hidden from the settings reflection pass.
        [IgnoreMenu]
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<bool> GauntletStop { get; set; }

        private static readonly string[] DefaultPriorities =
        {
            "40",  //  1. Ailment and Curse Reflection
            "30",  //  2. Blistering Cold
            "10",  //  3. Blistering Cold II
            "50",  //  4. Blistering Cold III
            "100", //  5. Blistering Cold IV
            "50",  //  6. Blood Altar
            "43",  //  7. Buffs Expire Faster
            "100", //  8. Choking Miasma
            "100", //  9. Choking Miasma II
            "100", // 10. Choking Miasma III
            "100", // 11. Choking Miasma IV
            "8",   // 12. Deadly Monsters
            "34",  // 13. Dexterous Monsters
            "100", // 14. Drought
            "59",  // 15. Escalating Damage Taken
            "5",   // 16. Escalating Monster Speed
            "14",  // 17. Hindering Flasks
            "18",  // 18. Impenetrable Monsters
            "58",  // 19. Impurity
            "31",  // 20. Lethal Rare Monsters
            "32",  // 21. Less Cooldown Recovery
            "53",  // 22. Lessened Reach
            "1",   // 23. Lightning Damage from Mana Costs
            "60",  // 24. Limited Arena
            "9",   // 25. Occasional Impotence
            "39",  // 26. Overwhelming Monsters
            "25",  // 27. Precise Monsters
            "44",  // 28. Prismatic Monsters
            "100", // 29. Profane Monsters
            "58",  // 30. Putrid Monsters
            "20",  // 31. Quicksand
            "10",  // 32. Quicksand II
            "100", // 33. Quicksand III
            "100", // 34. Quicksand IV
            "55",  // 35. Raging Dead
            "10",  // 36. Raging Dead II
            "50",  // 37. Raging Dead III
            "100", // 38. Raging Dead IV
            "8",   // 39. Random Projectiles
            "2",   // 40. Razor Dance
            "10",  // 41. Razor Dance II
            "50",  // 42. Razor Dance III
            "100", // 43. Razor Dance IV
            "90",  // 44. Reduced Recovery
            "17",  // 45. Resistant Monsters
            "10",  // 46. Restless Ground
            "10",  // 47. Restless Ground II
            "100", // 48. Restless Ground III
            "100", // 49. Restless Ground IV
            "100", // 50. Ruin
            "92",  // 51. Shattered Shield
            "13",  // 52. Shielding Monsters
            "58",  // 53. Siphoned Charges
            "5",   // 54. Siphoning Monsters
            "100", // 55. Stalking Ruin
            "100", // 56. Stalking Ruin II
            "100", // 57. Stalking Ruin III
            "100", // 58. Stalking Ruin IV
            "34",  // 59. Stormcaller Runes
            "10",  // 60. Stormcaller Runes II
            "50",  // 61. Stormcaller Runes III
            "100", // 62. Stormcaller Runes IV
            "63",  // 63. The Trialmaster
            "4",   // 64. Totem of Costly Might
            "3",   // 65. Totem of Costly Potency
            "11",  // 66. Treacherous Auras
            "16",  // 67. Unlucky Criticals
            "12",  // 68. Unstoppable Monsters
            "24",  // 69. Waning Spirit
        };

        private void DrawOptionPriorities()
        {
            if (Priorities == null)
            {
                return;
            }

            EnsureGauntletStopSize();

            ImGui.TextWrapped("1 = always take, higher = avoid. >= Avoid threshold = never take.");
            ImGui.TextWrapped("The checkbox marks a modifier as a Grueling Gauntlet stopper: " +
                              "when the game picks it, the plugin takes the rewards and ends the run " +
                              "instead of accepting the next trial. Used only while Grueling Gauntlet is on.");

            if (ImGui.Button("Reset to defaults"))
            {
                Priorities = new List<string>(DefaultPriorities);
                GauntletStop = new List<bool>(DefaultGauntletStop);
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear all stoppers"))
            {
                for (int i = 0; i < GauntletStop.Count; i++)
                {
                    GauntletStop[i] = false;
                }
            }

            int n = Math.Min(Priorities.Count, UltimatumMods.Length);
            for (int i = 0; i < n; i++)
            {
                // Checkbox first, slider after, both on one row. The checkbox
                // needs its own ImGui id or every row would share one.
                bool stop = i < GauntletStop.Count && GauntletStop[i];
                if (ImGui.Checkbox($"##gauntletStop{i}", ref stop))
                {
                    GauntletStop[i] = stop;
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Grueling Gauntlet: stop the run when this modifier is chosen");
                }

                ImGui.SameLine();

                int value = int.TryParse(Priorities[i], out int parsed) ? parsed : 20;
                if (ImGui.SliderInt(UltimatumMods[i], ref value, 1, 100))
                {
                    Priorities[i] = value.ToString();
                }
            }
        }

        // The stopper list has to line up with UltimatumMods by index. A config
        // saved by an older build has none, and one saved before a modifier was
        // added to the list has too few, so it is padded/trimmed on use rather
        // than trusted.
        private void EnsureGauntletStopSize()
        {
            GauntletStop ??= new List<bool>(UltimatumMods.Length);

            while (GauntletStop.Count < UltimatumMods.Length)
            {
                int i = GauntletStop.Count;
                GauntletStop.Add(i < DefaultGauntletStop.Length && DefaultGauntletStop[i]);
            }

            if (GauntletStop.Count > UltimatumMods.Length)
            {
                GauntletStop.RemoveRange(UltimatumMods.Length, GauntletStop.Count - UltimatumMods.Length);
            }
        }

        // True for a modifier that should end a Grueling Gauntlet run.
        public bool IsGauntletStopper(int modIndex)
        {
            if (modIndex < 0) return false;
            EnsureGauntletStopSize();
            return modIndex < GauntletStop.Count && GauntletStop[modIndex];
        }
    }
}
