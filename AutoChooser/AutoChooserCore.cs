using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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
        private bool _confirmed;
        private DateTime _lastHandle = DateTime.MinValue;
        private DateTime _panelOpenTime = DateTime.MinValue;
        private readonly Random _rng = new();

        public override bool Initialise()
        {
            Name = "AFK Ultimatum";
            return true;
        }

        public override void Render()
        {
            if (!Settings.Enable.Value)
            {
                _panelActive = false;
                return;
            }

            var panel = GameController?.IngameState?.IngameUi?.UltimatumPanel;
            if (panel == null || !panel.IsVisible)
            {
                // Panel closed: reset so the next open is treated as a fresh round.
                _panelActive = false;
                _confirmed = false;
                _lastHandle = DateTime.MinValue;
                return;
            }

            DateTime now = DateTime.UtcNow;

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

            // Already confirmed this round (panel may linger during the close
            // animation): do not act again and cause a stray click.
            if (_confirmed)
            {
                return;
            }

            // Act once, then retry if our Start click did not confirm and the panel
            // is still open (avoids the "did not pick on first try" problem).
            if ((now - _lastHandle).TotalMilliseconds >= Settings.RetryIntervalMs.Value)
            {
                _lastHandle = now;
                HandlePanel(panel);
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
                LogMessage("AutoChooser: Ultimatum panel visible but no choice elements found.");
                return;
            }

            int bestIndex = -1;
            int bestPriority = int.MaxValue;
            Element best = null;

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

                string name = i < modifierNames.Count ? modifierNames[i] : Normalize(el.GetText(1024) ?? string.Empty);
                int priority = GetPriority(name);

                if (Settings.Debug.Value)
                {
                    LogMessage($"AutoChooser: option[{i}] '{name}' priority={priority}");
                }

                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    bestIndex = i;
                    best = el;
                }
            }

            if (best == null)
            {
                LogMessage("AutoChooser: no valid/visible choice to click.");
                return;
            }

            if (bestPriority >= Settings.AvoidThreshold.Value && !Settings.ForcePickWhenAllAvoided.Value)
            {
                LogMessage($"AutoChooser: all options avoided (best priority {bestPriority}), not clicking.");
                return;
            }

            // Click the chosen card and verify it actually got selected. If the game
            // did not register the selection, retry once before confirming.
            ClickElement(best, $"option[{bestIndex}]");
            if (panel.SelectedChoice != bestIndex)
            {
                if (Settings.Debug.Value)
                {
                    LogMessage($"AutoChooser: option not selected yet (SelectedChoice={panel.SelectedChoice}, want {bestIndex}), retry");
                }

                Thread.Sleep(90);
                ClickElement(best, $"option[{bestIndex}] retry");
            }

            LogMessage($"AutoChooser: selected option[{bestIndex}] '{modifierNames.ElementAtOrDefault(bestIndex)}' (priority {bestPriority}).");

            Thread.Sleep(Settings.ClickDelayMs.Value);

            if (panel.ConfirmButton is Element confirm && confirm.IsValid && confirm.IsVisible)
            {
                ClickElement(confirm, "confirm/start");
                _confirmed = true;
                LogMessage("AutoChooser: pressed start/confirm.");
            }
            else if (Settings.Debug.Value)
            {
                LogMessage("AutoChooser: confirm/start button not found or not visible.");
            }
        }

        private static List<string> ReadModifierNames(UltimatumPanel panel)
        {
            var names = new List<string>(3);
            if (panel.Modifiers is IEnumerable mods)
            {
                foreach (var m in mods)
                {
                    names.Add(Normalize(m?.ToString() ?? string.Empty));
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

            for (int i = 0; i < AutoChooserSettings.UltimatumMods.Length; i++)
            {
                string baseName = Normalize(AutoChooserSettings.UltimatumMods[i]);
                if (baseName.Length == 0)
                {
                    continue;
                }

                if (norm.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private void ClickElement(Element el, string label)
        {
            RectangleF rect = el.GetClientRect();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            var window = GameController.Window.GetWindowRectangleTimeCache;
            Vector2 topLeft = window.TopLeft;
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
            "Choking Miasma", "Stormcaller Runes", "Raging Dead", "Blistering Cold",
            "Restless Ground", "Stalking Ruin", "Razor Dance", "Totem of Costly Might",
            "Totem of Costly Potency", "Blood Altar", "Quicksand", "The Trialmaster",
            "Limited Arena", "Ruin", "Reduced Recovery", "Lessened Reach",
            "Buffs Expire Faster", "Less Cooldown Recovery", "Escalating Damage Taken",
            "Escalating Monster Speed", "Profane Monsters", "Unlucky Criticals",
            "Hindering Flasks", "Drought", "Ailment and Curse Reflection",
            "Lightning Damage from Mana Costs", "Random Projectiles", "Treacherous Auras",
            "Occasional Impotence", "Siphoned Charges", "Impurity", "Waning Spirit",
            "Shattered Shield", "Unstoppable Monsters", "Lethal Rare Monsters",
            "Shielding Monsters", "Precise Monsters", "Overwhelming Monsters",
            "Deadly Monsters", "Prismatic Monsters", "Resistant Monsters",
            "Dexterous Monsters", "Siphoning Monsters", "Putrid Monsters",
            "Impenetrable Monsters"
        };

        public AutoChooserSettings()
        {
            Priorities = UltimatumMods.Select(_ => "20").ToList();
            OptionPriorityPanel.DrawDelegate = DrawOptionPriorities;
        }

        public ToggleNode Enable { get; set; } = new ToggleNode(false);

        [Menu("Priority >= this value means NEVER take (40 = default)", 1)]
        public RangeNode<int> AvoidThreshold { get; set; } = new RangeNode<int>(40, 1, 100);

        [Menu("If all 3 present options are avoided, pick best anyway", 2)]
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

        [Menu("Debug logging", 10)]
        public ToggleNode Debug { get; set; } = new ToggleNode(false);

        [JsonIgnore]
        [Menu("Ultimatum option priorities (1 = always, >= Avoid threshold = never)", 6)]
        public CustomNode OptionPriorityPanel { get; } = new CustomNode();

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> Priorities { get; set; }

        private void DrawOptionPriorities()
        {
            if (Priorities == null)
            {
                return;
            }

            ImGui.TextWrapped("1 = always take, higher = avoid. >= Avoid threshold = never take.");
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
