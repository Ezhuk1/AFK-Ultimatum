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
using ExileCore.Shared.AtlasHelper;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using RectangleF = SharpDX.RectangleF;

namespace AutoChooser
{
    public class AutoChooser : BaseSettingsPlugin<AutoChooserSettings>
    {
        private bool _panelActive;
        private DateTime _lastHandle = DateTime.MinValue;
        private DateTime _lastVisible = DateTime.MinValue;

        private const float IconSize = 20f;

        // mod (base name) -> game atlas texture key, captured from the live panel
        private readonly Dictionary<string, string> _iconNames = new(StringComparer.OrdinalIgnoreCase);
        // atlas texture key -> resolved atlas texture (cached)
        private readonly Dictionary<string, AtlasTexture> _iconCache = new(StringComparer.OrdinalIgnoreCase);

        public override bool Initialise()
        {
            Settings.Plugin = this;
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
                _lastHandle = DateTime.MinValue;
                return;
            }

            DateTime now = DateTime.UtcNow;
            _lastVisible = now;

            // Edge-detect the open: first frame the panel becomes visible we always act.
            if (!_panelActive)
            {
                _panelActive = true;
                _lastHandle = now;
                HandlePanel(panel);
                return;
            }

            // Panel still open from a previous round: retry if our Start click did not
            // confirm (panel lingers) and enough time has passed to avoid spamming.
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

                // Remember the icon for this modifier so the settings list can show it.
                int baseIdx = MatchBaseMod(name);
                if (baseIdx >= 0)
                {
                    string tex = FindIconTextureName(el);
                    if (!string.IsNullOrEmpty(tex))
                    {
                        _iconNames[AutoChooserSettings.UltimatumMods[baseIdx]] = tex;
                    }
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

            ClickElement(best, $"option[{bestIndex}]");
            LogMessage($"AutoChooser: selected option[{bestIndex}] '{modifierNames.ElementAtOrDefault(bestIndex)}' (priority {bestPriority}).");

            Thread.Sleep(Settings.ClickDelayMs.Value);

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
            Vector2 clickPos = rect.Center + topLeft;

            if (Settings.Debug.Value)
            {
                LogMessage($"AutoChooser: click {label} at screen ({clickPos.X:0},{clickPos.Y:0}) (winTopLeft {topLeft.X:0},{topLeft.Y:0}, center {rect.Center.X:0},{rect.Center.Y:0})");
            }

            try
            {
                NativeMouse.SetCursorPos((int)Math.Round(clickPos.X), (int)Math.Round(clickPos.Y));
                Thread.Sleep(15);
                NativeMouse.LeftClick();
            }
            catch (Exception ex)
            {
                LogMessage($"AutoChooser: click failed: {ex.Message}");
            }
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

        private static string FindIconTextureName(Element el)
        {
            if (el == null)
            {
                return null;
            }

            var self = el.TextureName;
            if (!string.IsNullOrEmpty(self))
            {
                return self;
            }

            var children = el.Children;
            if (children != null)
            {
                foreach (var c in children)
                {
                    var t = c?.TextureName;
                    if (!string.IsNullOrEmpty(t))
                    {
                        return t;
                    }

                    var grandchildren = c?.Children;
                    if (grandchildren != null)
                    {
                        foreach (var c2 in grandchildren)
                        {
                            var t2 = c2?.TextureName;
                            if (!string.IsNullOrEmpty(t2))
                            {
                                return t2;
                            }
                        }
                    }
                }
            }

            return null;
        }

        internal bool TryDrawIcon(string baseModName)
        {
            if (string.IsNullOrEmpty(baseModName) ||
                !_iconNames.TryGetValue(baseModName, out var texName) ||
                string.IsNullOrEmpty(texName))
            {
                return false;
            }

            if (!_iconCache.TryGetValue(texName, out var atlas))
            {
                try
                {
                    atlas = GetAtlasTexture(texName);
                }
                catch
                {
                    atlas = null;
                }

                _iconCache[texName] = atlas;
            }

            if (atlas == null)
            {
                return false;
            }

            var graphics = Graphics;
            if (graphics == null)
            {
                return false;
            }

            IntPtr texId;
            try
            {
                texId = graphics.GetTextureId(atlas.AtlasFileName);
            }
            catch
            {
                return false;
            }

            if (texId == IntPtr.Zero)
            {
                return false;
            }

            var uv = atlas.TextureUV;
            ImGui.Image(
                texId,
                new System.Numerics.Vector2(IconSize, IconSize),
                new System.Numerics.Vector2(uv.X, uv.Y),
                new System.Numerics.Vector2(uv.Right, uv.Bottom));
            ImGui.SameLine(0, 4);
            return true;
        }

        private static class NativeMouse
        {
            [DllImport("user32.dll", EntryPoint = "SetCursorPos")]
            private static extern bool SetCursorPosNative(int x, int y);

            [DllImport("user32.dll")]
            private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

            private const int MouseEventLeftDown = 0x02;
            private const int MouseEventLeftUp = 0x04;

            public static void SetCursorPos(int x, int y)
            {
                SetCursorPosNative(x, y);
            }

            public static void LeftClick()
            {
                mouse_event(MouseEventLeftDown, 0, 0, 0, 0);
                Thread.Sleep(10);
                mouse_event(MouseEventLeftUp, 0, 0, 0, 0);
            }
        }
    }

    public class AutoChooserSettings : ISettings
    {
        [JsonIgnore]
        internal AutoChooser Plugin;
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

        [Menu("Retry interval while panel stays open (ms)", 5)]
        public RangeNode<int> RetryIntervalMs { get; set; } = new RangeNode<int>(1500, 200, 10000);

        [Menu("Debug logging", 6)]
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
                Plugin?.TryDrawIcon(UltimatumMods[i]);
                int value = int.TryParse(Priorities[i], out int parsed) ? parsed : 20;
                if (ImGui.SliderInt(UltimatumMods[i], ref value, 1, 100))
                {
                    Priorities[i] = value.ToString();
                }
            }
        }
    }
}
