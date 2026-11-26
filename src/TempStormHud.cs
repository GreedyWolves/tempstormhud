using System;
using System.Globalization;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace TempStormHud
{
    public class TempStormHudSystem : ModSystem
    {
        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        const string Domain = "tempstormhud:";

        ICoreClientAPI capi;
        ModSystem tempSys;
        ModSystem riftSys;
        HudTempStorm hud;
        bool userVisible = true;
        string lastText;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            hud = new HudTempStorm(api);

            api.Input.RegisterHotKey("tempstormhud", L("hotkey-toggle"),
                GlKeys.T, HotkeyType.GUIOrOtherControls, ctrlPressed: true, shiftPressed: true);
            api.Input.SetHotKeyHandler("tempstormhud", _ =>
            {
                userVisible = !userVisible;
                Update(0);
                return true;
            });

            api.Event.RegisterGameTickListener(Update, 1000);
        }

        void Update(float dt)
        {
            // Look the systems up by name and read their data via reflection:
            // no hard compile-time dependency on VSSurvivalMod.dll, and resilient
            // to fields becoming properties (or vice versa) between game versions.
            tempSys ??= capi.ModLoader.GetModSystem("Vintagestory.GameContent.SystemTemporalStability");
            riftSys ??= capi.ModLoader.GetModSystem("Vintagestory.GameContent.ModSystemRiftWeather");

            string text = null;
            if (userVisible && capi.World?.Calendar != null)
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

        static string L(string key, params object[] args) => Lang.Get(Domain + key, args);

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
            hud?.TryClose();
            hud?.Dispose();
            base.Dispose();
        }
    }

    public class HudTempStorm : HudElement
    {
        public override string ToggleKeyCombinationCode => null;

        int lineCount;

        public HudTempStorm(ICoreClientAPI capi) : base(capi)
        {
            ComposeFor("Temporal Storm HUD", 1);
        }

        // The background only fits the text when composing,
        // so the HUD is rebuilt whenever the number of lines changes.
        void ComposeFor(string text, int lines)
        {
            lineCount = lines;
            SingleComposer?.Dispose();

            ElementBounds textBounds = ElementBounds.Fixed(0, 0, 380, 20 * lines);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(8);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(textBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.LeftTop)
                .WithFixedAlignmentOffset(10, 10);

            double[] bgColor = (double[])GuiStyle.DialogDefaultBgColor.Clone();
            bgColor[3] = 0.5;

            // Non-empty text: the richtext computes its height from the content,
            // and with "" the background ended up with zero height.
            SingleComposer = capi.Gui.CreateCompo("tempstormhud", dialogBounds)
                .AddGameOverlay(bgBounds, bgColor)
                .BeginChildElements(bgBounds)
                    .AddRichtext(text, CairoFont.WhiteSmallText(), textBounds, "text")
                .EndChildElements()
                .Compose();
        }

        public void SetText(string text)
        {
            int lines = text.Split("<br>").Length;
            if (lines != lineCount) ComposeFor(text, lines);
            else SingleComposer.GetRichtext("text").SetNewText(text, CairoFont.WhiteSmallText());
        }
    }
}
