using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared.Attributes;
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
        private DateTime _lootPhaseStart = DateTime.MinValue;
        private DateTime _lastLootClick = DateTime.MinValue;
        private bool _panelWasVisible;
        private bool _panelWasVisible;

        public override bool Initialise()
        {
            Name = "AFK Ultimatum";
            return true;
        }

        private bool _pauseHotkeyWasPressed;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private bool CheckPauseHotkey()
        {
            int vk = (int)(Keys)Settings.PauseHotkey.Value;
            bool hotkeyDown = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (hotkeyDown && !_pauseHotkeyWasPressed)
            {
                _pauseUntil = DateTime.UtcNow.AddMilliseconds(Settings.PauseDurationMs.Value);
                _panelActive = false;
                _lootPhaseActive = false;
                _votedThisRound = false;
                _lastHandle = DateTime.MinValue;
                _followerWaitStart = DateTime.MinValue;
                _pauseHotkeyWasPressed = true;
                LogMessage($"AutoChooser: paused for {Settings.PauseDurationMs.Value} ms.");
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
                _panelWasVisible = false;
                _pauseUntil = DateTime.MinValue;
                _pauseHotkeyWasPressed = false;
                return;
            }

            CheckPauseHotkey();

            if (DateTime.UtcNow < _pauseUntil)
            {
                return;
            }

            var panel = GameController?.IngameState?.IngameUi?.UltimatumPanel;

            // Loot pickup phase: panel is gone, click visible ground items.
            if (_lootPhaseActive)
            {
                if (panel != null && panel.IsVisible)
                {
                    _lootPhaseActive = false;
                    return;
                }

                DateTime lootNow = DateTime.UtcNow;

                if ((lootNow - _lootPhaseStart).TotalMilliseconds >= Settings.LootPickupTimeoutMs.Value)
                {
                    _lootPhaseActive = false;
                    LogMessage("AutoChooser: loot pickup ended (timeout).");
                    return;
                }

                if ((lootNow - _lastLootClick).TotalMilliseconds >= Settings.LootPickupIntervalMs.Value)
                {
                    _lastLootClick = lootNow;
                    TryPickupLoot();
                }

                return;
            }

            if (panel == null || !panel.IsVisible)
            {
                if (_panelActive && Settings.LootPickupEnabled.Value)
                {
                    _lootPhaseActive = true;
                    _lootPhaseStart = DateTime.UtcNow;
                    _lastLootClick = DateTime.MinValue;
                    LogMessage("AutoChooser: panel closed, starting loot pickup.");
                }

                _panelActive = false;
                _votedThisRound = false;
                _lastHandle = DateTime.MinValue;
                _followerWaitStart = DateTime.MinValue;
                return;
            }

            DateTime now = DateTime.UtcNow;

            bool panelVisible = panel != null && panel.IsVisible;

            // Panel just reappeared after being invisible (between rounds) — reset vote.
            if (panelVisible && !_panelWasVisible)
            {
                _votedThisRound = false;
                _lastHandle = DateTime.MinValue;
                _followerWaitStart = DateTime.MinValue;
            }

            _panelWasVisible = panelVisible;

            // Edge-detect the open: the first frame the panel becomes visible we just
            // mark it and wait a short settle delay so the UI is fully interactive.
            if (!_panelActive)
            {
                _panelActive = true;
                _panelOpenTime = now;
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
                    LogMessage($"AutoChooser: handle failed: {ex.Message}");
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

            if (choices.Count == 0)
            {
                // No choice cards — may be a "Begin" / "Next wave" screen with just a confirm button.
                if (panel.ConfirmButton is Element confirm2 && confirm2.IsValid && confirm2.IsVisible)
                {
                    ClickElement(confirm2, "confirm/begin");
                    LogMessage("AutoChooser: no choices visible, pressed confirm/begin.");
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
                    if (Settings.Debug.Value)
                    {
                        LogMessage("AutoChooser: not in a party, voting by own priority.");
                    }

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
                        if (Settings.Debug.Value)
                        {
                            LogMessage($"AutoChooser: following leading vote -> option[{pickIndex}] (count {count}).");
                        }
                    }
                    else
                    {
                        if (_followerWaitStart == DateTime.MinValue)
                        {
                            _followerWaitStart = DateTime.UtcNow;
                        }

                        if ((DateTime.UtcNow - _followerWaitStart).TotalMilliseconds >= FollowerTimeoutMs)
                        {
                            if (Settings.Debug.Value)
                            {
                                LogMessage("AutoChooser: no votes detected in time, falling back to own priority.");
                            }

                            (pickIndex, pick, pickPriority) = PickByPriority(choices, modifierNames);
                        }
                        else
                        {
                            if (Settings.Debug.Value)
                            {
                                LogMessage($"AutoChooser: follower waiting for party votes ({(int)(DateTime.UtcNow - _followerWaitStart).TotalMilliseconds} ms).");
                            }

                            return;
                        }
                    }
                }
            }

            if (pick == null)
            {
                LogMessage("AutoChooser: no selectable option (all set to never, or none visible); not clicking.");
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
                    if (Settings.Debug.Value)
                    {
                        LogMessage($"AutoChooser: option not selected yet (SelectedChoice={panel.SelectedChoice}, want {pickIndex}), retry");
                    }

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
                LogMessage($"AutoChooser: selected option[{pickIndex}] '{pickedName}' (priority {pickPriority}).");
                _votedThisRound = true;
                Thread.Sleep(Settings.ClickDelayMs.Value);
            }

            // Click Confirm on every pass. In a party it stays disabled until everyone
            // has voted, so the click is a no-op until then and succeeds once enabled
            // (the panel then closes and our per-round state resets).
            if (panel.ConfirmButton is Element confirm && confirm.IsValid && confirm.IsVisible)
            {
                ClickElement(confirm, "confirm/start");
                LogMessage("AutoChooser: pressed start/confirm.");
            }
            else if (Settings.Debug.Value)
            {
                LogMessage("AutoChooser: confirm/start button not found or not visible.");
            }
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

                if (Settings.Debug.Value)
                {
                    LogMessage($"AutoChooser: option[{i}] '{name}' priority={priority}");
                }

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
                        if (Settings.Debug.Value)
                        {
                            LogMessage($"AutoChooser: in party detected (status {statusName}).");
                        }

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
                LogMessage($"AutoChooser: party check failed: {ex.Message}");
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


        private int GetPriority(string modifierName)
        {
            if (string.IsNullOrWhiteSpace(modifierName))
            {
                return Settings.DefaultPriority.Value;
            }

            string norm = Normalize(modifierName);
            int idx = MatchBaseMod(norm);
            var priorities = Settings.Priorities;
            if (idx >= 0 && priorities != null && idx < priorities.Count &&
                int.TryParse(priorities[idx], out int p))
            {
                return p;
            }

            return Settings.DefaultPriority.Value;
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

        private void TryPickupLoot()
        {
            try
            {
                var labels = GameController?.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible;
                if (labels == null || labels.Count == 0)
                {
                    _lootPhaseActive = false;
                    if (Settings.Debug.Value)
                    {
                        LogMessage("AutoChooser: loot pickup ended (no visible labels).");
                    }

                    return;
                }

                var window = GameController?.Window;
                if (window == null) return;

                Vector2 windowTopLeft = window.GetWindowRectangleTimeCache.TopLeft;
                int maxDist = Settings.LootPickupMaxDistance.Value;

                LabelOnGround best = null;
                float bestDist = float.MaxValue;

                for (int i = 0; i < labels.Count; i++)
                {
                    var label = labels[i];
                    if (label?.ItemOnGround == null || !label.ItemOnGround.IsValid) continue;

                    float dist = label.ItemOnGround.DistancePlayer;
                    if (dist > maxDist) continue;

                    if (label?.Label == null || !label.Label.IsValid) continue;
                    var rect = label.Label.GetClientRect();
                    if (rect.Width <= 0 || rect.Height <= 0) continue;

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = label;
                    }
                }

                if (best == null)
                {
                    _lootPhaseActive = false;
                    if (Settings.Debug.Value)
                    {
                        LogMessage("AutoChooser: loot pickup ended (no items in range).");
                    }

                    return;
                }

                var bestRect = best.Label.GetClientRect();
                Vector2 center = bestRect.Center + windowTopLeft;

                int j = Settings.ClickJitter.Value;
                int jx = j > 0 ? _rng.Next(-j, j + 1) : 0;
                int jy = j > 0 ? _rng.Next(-j, j + 1) : 0;
                int x = (int)Math.Round(center.X) + jx;
                int y = (int)Math.Round(center.Y) + jy;

                if (Settings.Debug.Value)
                {
                    LogMessage($"AutoChooser: loot click at ({x},{y}) dist={bestDist:0}");
                }

                MoveMouseSmooth(x, y);
                if (DateTime.UtcNow < _pauseUntil) return;
                Thread.Sleep(20 + _rng.Next(0, 40));
                NativeMouse.LeftClick();
            }
            catch (Exception ex)
            {
                LogMessage($"AutoChooser: loot pickup failed: {ex.Message}");
            }
        }

        private void ClickElement(Element el, string label)
        {
            RectangleF rect = el.GetClientRect();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            var window = GameController?.Window;
            if (window == null)
            {
                return;
            }

            var rectCache = window.GetWindowRectangleTimeCache;
            Vector2 topLeft = rectCache.TopLeft;
            Vector2 center = rect.Center + topLeft;

            int j = Settings.ClickJitter.Value;
            int jx = j > 0 ? _rng.Next(-j, j + 1) : 0;
            int jy = j > 0 ? _rng.Next(-j, j + 1) : 0;
            int x = (int)Math.Round(center.X) + jx;
            int y = (int)Math.Round(center.Y) + jy;

            if (Settings.Debug.Value)
            {
                LogMessage($"AutoChooser: click {label} at screen ({x},{y}) (winTopLeft {topLeft.X:0},{topLeft.Y:0}, center {center.X:0},{center.Y:0})");
            }

            try
            {
                MoveMouseSmooth(x, y);
                if (DateTime.UtcNow < _pauseUntil) return;
                Thread.Sleep(20 + _rng.Next(0, 40));
                NativeMouse.LeftClick();
            }
            catch (Exception ex)
            {
                LogMessage($"AutoChooser: click failed: {ex.Message}");
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
            OptionPriorityPanel.DrawDelegate = DrawOptionPriorities;
        }

        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        [Menu("This client is the party leader (picks the modifier). Uncheck to follow the party leader's vote", 0)]
        public ToggleNode PartyLeader { get; set; } = new ToggleNode(true);

        [Menu("Hotkey to pause the bot for the duration set below", 11)]
        public HotkeyNode PauseHotkey { get; set; } = new HotkeyNode(Keys.F);

        [Menu("Pause duration after the hotkey press (ms)", 12)]
        public RangeNode<int> PauseDurationMs { get; set; } = new RangeNode<int>(6000, 500, 60000);

        [Menu("Only act when the game window is in the foreground (safe AFK)", 1)]
        public ToggleNode OnlyWhenGameFocused { get; set; } = new ToggleNode(true);

        [Menu("If all 3 present options are set to 100 (never), pick least-bad anyway", 2)]
        public ToggleNode ForcePickWhenAllAvoided { get; set; } = new ToggleNode(true);

        [Menu("Priority used when a modifier is not in the list", 3)]
        public RangeNode<int> DefaultPriority { get; set; } = new RangeNode<int>(20, 1, 100);

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

        [Menu("Loot pickup", 13)]
        public ToggleNode LootPickupEnabled { get; set; } = new ToggleNode(true);

        [Menu("Loot pickup timeout (ms) — stops picking after this time", 14)]
        public RangeNode<int> LootPickupTimeoutMs { get; set; } = new RangeNode<int>(8000, 1000, 30000);

        [Menu("Loot pickup click interval (ms)", 15)]
        public RangeNode<int> LootPickupIntervalMs { get; set; } = new RangeNode<int>(200, 50, 2000);

        [Menu("Loot pickup max distance (px from player)", 16)]
        public RangeNode<int> LootPickupMaxDistance { get; set; } = new RangeNode<int>(300, 50, 800);

        [Menu("Debug logging", 10)]
        public ToggleNode Debug { get; set; } = new ToggleNode(false);

        [JsonIgnore]
        [Menu("Ultimatum option priorities (1 = always, >= Avoid threshold = never)", 6)]
        public CustomNode OptionPriorityPanel { get; } = new CustomNode();

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> Priorities { get; set; }

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

            ImGui.TextWrapped("1 = always take, higher = avoid. >= Avoid threshold = never take.");

            if (ImGui.Button("Reset to defaults"))
            {
                Priorities = new List<string>(DefaultPriorities);
            }

            int n = Math.Min(Priorities.Count, UltimatumMods.Length);
            for (int i = 0; i < n; i++)
            {
                int value = int.TryParse(Priorities[i], out int parsed) ? parsed : 20;
                if (ImGui.SliderInt(UltimatumMods[i], ref value, 1, 100))
                {
                    Priorities[i] = value.ToString();
                }
            }
        }
    }
}
