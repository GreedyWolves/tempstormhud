using System;
using System.Globalization;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace TempStormHud
{
    // Stored in VintagestoryData/ModConfig/tempstormhud.json. Edited through the settings dialog.
    public class TempStormHudConfig
    {
        public int ConfigVersion = 1;
        public bool ShowHud = true;
        public string Position = "LeftTop";
    }

    public class TempStormHudSystem : ModSystem
    {
        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const string Domain = "tempstormhud:";
        const string ConfigFile = "tempstormhud.json";
        public const string HotkeyCode = "tempstormhud";

        // Positions offered in the settings dialog (bottom center is taken by the hotbar).
        public static readonly EnumDialogArea[] Positions =
        {
            EnumDialogArea.LeftTop, EnumDialogArea.CenterTop, EnumDialogArea.RightTop,
            EnumDialogArea.LeftMiddle, EnumDialogArea.RightMiddle,
            EnumDialogArea.LeftBottom, EnumDialogArea.RightBottom
        };

        ICoreClientAPI capi;
        ModSystem tempSys;
        ModSystem riftSys;
        HudTempStorm hud;
        GuiDialogTempStormHudSettings settings;
        string lastText;

        public TempStormHudConfig Config { get; private set; }
        public EnumDialogArea Position { get; private set; } = EnumDialogArea.LeftTop;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            LoadConfig();

            hud = new HudTempStorm(api, Position);
            settings = new GuiDialogTempStormHudSettings(api, this);

            // The hotkey code stays "tempstormhud" so players keep their key bindings.
            api.Input.RegisterHotKey(HotkeyCode, L("hotkey-settings"),
                GlKeys.T, HotkeyType.GUIOrOtherControls, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler(HotkeyCode, _ =>
            {
                settings.Toggle();
                return true;
            });

            api.Event.RegisterGameTickListener(Update, 1000);
        }

        void LoadConfig()
        {
            try
            {
                Config = capi.LoadModConfig<TempStormHudConfig>(ConfigFile);
            }
            catch (Exception e)
            {
                // Keep the broken file as it is (the player may have edited it by hand);
                // it is only overwritten once a setting is changed in the dialog.
                Mod.Logger.Warning("Could not read ModConfig/{0}, using default settings: {1}", ConfigFile, e.Message);
                Config = new TempStormHudConfig();
            }

            if (Config == null)
            {
                Config = new TempStormHudConfig();
                SaveConfig();
            }

            Position = ParsePosition(Config.Position);
        }

        void SaveConfig()
        {
            try
            {
                capi.StoreModConfig(Config, ConfigFile);
            }
            catch (Exception e)
            {
                Mod.Logger.Warning("Could not save ModConfig/{0}: {1}", ConfigFile, e.Message);
            }
        }

        static EnumDialogArea ParsePosition(string value)
        {
            foreach (EnumDialogArea area in Positions)
            {
                if (area.ToString() == value) return area;
            }
            return EnumDialogArea.LeftTop;
        }

        public void SetShowHud(bool show)
        {
            if (Config.ShowHud == show) return;
            Config.ShowHud = show;
            SaveConfig();
            Update(0);
        }

        public void SetPosition(EnumDialogArea area)
        {
            if (Array.IndexOf(Positions, area) < 0 || area == Position) return;
            Position = area;
            Config.Position = area.ToString();
            SaveConfig();
            hud.SetPosition(area);
        }

        void Update(float dt)
        {
            // Look the systems up by name and read their data via reflection:
            // no hard compile-time dependency on VSSurvivalMod.dll, and resilient
            // to fields becoming properties (or vice versa) between game versions.
            tempSys ??= capi.ModLoader.GetModSystem("Vintagestory.GameContent.SystemTemporalStability");
            riftSys ??= capi.ModLoader.GetModSystem("Vintagestory.GameContent.ModSystemRiftWeather");

            string text = null;
            if (Config.ShowHud && capi.World?.Calendar != null)
            {
                string storm = BuildStormLine();
                string rift = BuildRiftLine();
                if (storm != null && rift != null) text = storm + "<br><br>" + rift;
                else text = storm ?? rift;
            }

            if (text == null)
            {
                if (hud.IsOpened()) hud.TryClose();
                return;
            }

            if (!hud.IsOpened()) hud.TryOpen();
            if (text != lastText)
            {
                hud.SetText(text);
                lastText = text;
            }
        }

        string BuildRiftLine()
        {
            // Private field of ModSystemRiftWeather; null until the server sends its packet.
            object pattern = Get(riftSys, "curPattern");
            if (pattern == null) return null;

            string code = Get(pattern, "Code")?.ToString();
            if (string.IsNullOrEmpty(code)) return null;

            // The game's own translation, with only the first letter lowercased.
            string level = Lang.Get("rift-activity-" + code);
            if (level.Length > 0) level = char.ToLower(level[0], Culture) + level.Substring(1);

            double hoursLeft = Convert.ToDouble(Get(pattern, "UntilTotalHours")) - capi.World.Calendar.ElapsedHours;
            return L("rift-activity", level, FormatHours(hoursLeft));
        }

        string BuildStormLine()
        {
            if (tempSys == null) return null;
            if (capi.World.Config?.GetString("temporalStorms") == "off") return null;

            object data = Get(tempSys, "StormData");
            if (data == null) return null;

            double now = capi.World.Calendar.TotalDays;
            float hoursPerDay = capi.World.Calendar.HoursPerDay;

            bool active = Convert.ToBoolean(Get(data, "nowStormActive"));
            string strength = Strength(Get(data, "nextStormStrength")?.ToString());

            if (active)
            {
                double endDays = Convert.ToDouble(Get(data, "stormActiveTotalDays"));
                return L("storm-active", strength, FormatTime(endDays - now, hoursPerDay));
            }

            double nextDays = Convert.ToDouble(Get(data, "nextStormTotalDays"));
            return L("storm-upcoming", strength, FormatTime(nextDays - now, hoursPerDay));
        }

        internal static string L(string key, params object[] args) => Lang.Get(Domain + key, args);

        static string cultureLocale;
        static CultureInfo culture = CultureInfo.InvariantCulture;

        // Number formatting follows the game's language (e.g. "18.0" in English, "18,0" in Polish).
        // Lang.Get formats with the thread's culture, so numbers are formatted here beforehand.
        static CultureInfo Culture
        {
            get
            {
                string locale = Lang.CurrentLocale;
                if (locale != cultureLocale)
                {
                    cultureLocale = locale;
                    try { culture = CultureInfo.GetCultureInfo(locale ?? ""); }
                    catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
                }
                return culture;
            }
        }

        static string FormatTime(double days, float hoursPerDay)
        {
            if (days <= 0) return L("time-moment");
            if (days >= 1) return L("time-days", days.ToString("0.0", Culture));
            return FormatHours(days * hoursPerDay);
        }

        static string FormatHours(double hours)
        {
            if (hours <= 0) return L("time-moment");
            int h = (int)hours;
            int m = (int)((hours - h) * 60);
            return L("time-hours", h.ToString(Culture), m.ToString("00", Culture));
        }

        static string Strength(string s) => s switch
        {
            "Light" => L("strength-light"),
            "Medium" => L("strength-medium"),
            "Heavy" => L("strength-heavy"),
            _ => "?"
        };

        static object Get(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            PropertyInfo p = t.GetProperty(name, BF);
            if (p != null) return p.GetValue(obj);
            FieldInfo f = t.GetField(name, BF);
            return f?.GetValue(obj);
        }

        public override void Dispose()
        {
            settings?.TryClose();
            settings?.Dispose();
            hud?.TryClose();
            hud?.Dispose();
            base.Dispose();
        }
    }

    public class HudTempStorm : HudElement
    {
        const double Margin = 10;

        public override string ToggleKeyCombinationCode => null;

        int lineCount;
        string text = "Temporal Storm HUD";
        EnumDialogArea area;

        public HudTempStorm(ICoreClientAPI capi, EnumDialogArea area) : base(capi)
        {
            this.area = area;
            ComposeFor(text, 1);
        }

        public void SetPosition(EnumDialogArea newArea)
        {
            if (newArea == area) return;
            area = newArea;
            ComposeFor(text, lineCount);
        }

        // The offset is added after alignment (ElementBounds.calcMarginFromAlignment),
        // so it points inwards: negative from the right/bottom edge, zero on a centered axis.
        static double OffsetX(EnumDialogArea a)
        {
            switch (a)
            {
                case EnumDialogArea.LeftTop:
                case EnumDialogArea.LeftMiddle:
                case EnumDialogArea.LeftBottom:
                    return Margin;
                case EnumDialogArea.RightTop:
                case EnumDialogArea.RightMiddle:
                case EnumDialogArea.RightBottom:
                    return -Margin;
                default:
                    return 0;
            }
        }

        static double OffsetY(EnumDialogArea a)
        {
            switch (a)
            {
                case EnumDialogArea.LeftTop:
                case EnumDialogArea.CenterTop:
                case EnumDialogArea.RightTop:
                    return Margin;
                case EnumDialogArea.LeftBottom:
                case EnumDialogArea.CenterBottom:
                case EnumDialogArea.RightBottom:
                    return -Margin;
                default:
                    return 0;
            }
        }

        // The background only fits the text when composing,
        // so the HUD is rebuilt whenever the number of lines (or the position) changes.
        void ComposeFor(string newText, int lines)
        {
            text = newText;
            lineCount = lines;
            SingleComposer?.Dispose();

            ElementBounds textBounds = ElementBounds.Fixed(0, 0, 380, 20 * lines);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(8);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(textBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(area)
                .WithFixedAlignmentOffset(OffsetX(area), OffsetY(area));

            double[] bgColor = (double[])GuiStyle.DialogDefaultBgColor.Clone();
            bgColor[3] = 0.5;

            // Non-empty text: the richtext computes its height from the content,
            // and with "" the background ended up with zero height.
            SingleComposer = capi.Gui.CreateCompo("tempstormhud", dialogBounds)
                .AddGameOverlay(bgBounds, bgColor)
                .BeginChildElements(bgBounds)
                    .AddRichtext(newText, CairoFont.WhiteSmallText(), textBounds, "text")
                .EndChildElements()
                .Compose();
        }

        public void SetText(string newText)
        {
            int lines = newText.Split("<br>").Length;
            if (lines != lineCount) ComposeFor(newText, lines);
            else
            {
                text = newText;
                SingleComposer.GetRichtext("text").SetNewText(newText, CairoFont.WhiteSmallText());
            }
        }
    }

    // Settings window, opened with the mod's hotkey. Every change is saved and applied right away.
    public class GuiDialogTempStormHudSettings : GuiDialog
    {
        readonly TempStormHudSystem system;

        // Lets the focused dialog close itself on the same hotkey (GuiDialog.OnKeyDown).
        public override string ToggleKeyCombinationCode => TempStormHudSystem.HotkeyCode;

        public GuiDialogTempStormHudSettings(ICoreClientAPI capi, TempStormHudSystem system) : base(capi)
        {
            this.system = system;
        }

        public override void OnGuiOpened()
        {
            // Composed on every opening, so it always shows the current settings and language.
            Compose();
            base.OnGuiOpened();
        }

        static string L(string key, params object[] args) => TempStormHudSystem.L(key, args);

        void Compose()
        {
            const double labelWidth = 140;
            const double controlX = 150;
            const double controlWidth = 220;
            const double rowHeight = 30;
            const double rowGap = 15;

            double y = GuiStyle.TitleBarHeight + 10;
            ElementBounds showLabelBounds = ElementBounds.Fixed(0, y + 5, labelWidth, rowHeight);
            ElementBounds showSwitchBounds = ElementBounds.Fixed(controlX, y, rowHeight, rowHeight);

            y += rowHeight + rowGap;
            ElementBounds positionLabelBounds = ElementBounds.Fixed(0, y + 5, labelWidth, rowHeight);
            ElementBounds positionBounds = ElementBounds.Fixed(controlX, y, controlWidth, rowHeight);

            y += rowHeight + rowGap + 10;
            // Left side, so the expanded position list doesn't cover it.
            ElementBounds closeBounds = ElementBounds.Fixed(0, y, 0, 0)
                .WithFixedPadding(10, 2);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(showLabelBounds, showSwitchBounds, positionLabelBounds, positionBounds, closeBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            EnumDialogArea[] positions = TempStormHudSystem.Positions;
            string[] values = new string[positions.Length];
            string[] names = new string[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                values[i] = positions[i].ToString();
                names[i] = L("position-" + values[i].ToLowerInvariant());
            }
            int selected = Math.Max(0, Array.IndexOf(positions, system.Position));

            SingleComposer?.Dispose();
            SingleComposer = capi.Gui.CreateCompo("tempstormhud-settings", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(L("settings-title"), () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddStaticText(L("settings-show"), CairoFont.WhiteSmallText(), showLabelBounds)
                    .AddSwitch(system.SetShowHud, showSwitchBounds, "showhud")
                    .AddStaticText(L("settings-position"), CairoFont.WhiteSmallText(), positionLabelBounds)
                    .AddDropDown(values, names, selected, OnPositionChanged, positionBounds, "position")
                    .AddSmallButton(L("settings-close"), () => TryClose(), closeBounds)
                .EndChildElements()
                .Compose();

            SingleComposer.GetSwitch("showhud").SetValue(system.Config.ShowHud);
        }

        void OnPositionChanged(string code, bool selected)
        {
            if (!selected) return;
            if (Enum.TryParse(code, out EnumDialogArea area)) system.SetPosition(area);
        }
    }
}
