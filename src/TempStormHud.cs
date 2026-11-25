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

            api.Input.RegisterHotKey("tempstormhud", "Pokaż/ukryj licznik burzy temporalnej",
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
            // Pobieramy system po nazwie i czytamy dane przez refleksję:
            // brak twardej zależności od VSSurvivalMod.dll przy kompilacji
            // i odporność na zamianę pól na właściwości między wersjami gry.
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
            // Prywatne pole ModSystemRiftWeather; null, dopóki serwer nie przyśle pakietu.
            object pattern = Get(riftSys, "curPattern");
            if (pattern == null) return null;

            string code = Get(pattern, "Code")?.ToString();
            if (string.IsNullOrEmpty(code)) return null;

            // Tłumaczenie z gry, tylko pierwsza litera zmniejszona.
            string level = Lang.Get("rift-activity-" + code);
            if (level.Length > 0) level = char.ToLower(level[0], Pl) + level.Substring(1);

            double hoursLeft = Convert.ToDouble(Get(pattern, "UntilTotalHours")) - capi.World.Calendar.ElapsedHours;
            return $"Aktywność wyrw: <strong>{level}</strong>, przez <strong>{FormatHours(hoursLeft)}</strong>";
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
            string strength = StrengthPl(Get(data, "nextStormStrength")?.ToString());

            if (active)
            {
                double endDays = Convert.ToDouble(Get(data, "stormActiveTotalDays"));
                return $"Burza temporalna ({strength}) trwa jeszcze <strong>{FormatTime(endDays - now, hoursPerDay)}</strong>";
            }

            double nextDays = Convert.ToDouble(Get(data, "nextStormTotalDays"));
            return $"Burza temporalna ({strength}) za <strong>{FormatTime(nextDays - now, hoursPerDay)}</strong>";
        }

        static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

        static string FormatTime(double days, float hoursPerDay)
        {
            if (days <= 0) return "chwilę";
            if (days >= 1) return days.ToString("0.0", Pl) + " dnia";
            return FormatHours(days * hoursPerDay);
        }

        static string FormatHours(double hours)
        {
            if (hours <= 0) return "chwilę";
            int h = (int)hours;
            int m = (int)((hours - h) * 60);
            return $"{h} h {m:00} min";
        }

        static string StrengthPl(string s) => s switch
        {
            "Light" => "lekka",
            "Medium" => "średnia",
            "Heavy" => "ciężka",
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
            ComposeFor("Burza temporalna", 1);
        }

        // Tło dopasowuje się do tekstu tylko przy komponowaniu,
        // więc przy zmianie liczby linii budujemy HUD od nowa.
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

            // Niepusty tekst: richtext liczy wysokość z treści,
            // a przy "" tło dopasowywało się do zerowej wysokości.
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
