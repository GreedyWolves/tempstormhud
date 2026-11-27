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
        // 2: OffsetX/OffsetY added (custom position set by dragging the HUD).
        public int ConfigVersion = 2;
        public bool ShowHud = true;
        // EnumDialogArea name: the screen edge/corner the HUD is anchored to.
        public string Position = "LeftTop";
        // Custom position: offset from the anchor in GUI units (ElementBounds.fixedOffsetX/Y).
        // Both null = a preset position with the default margin.
        public double? OffsetX;
        public double? OffsetY;
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
        public bool IsCustomPosition => Config.OffsetX.HasValue && Config.OffsetY.HasValue;

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            LoadConfig();

            hud = new HudTempStorm(api, Position, Config.OffsetX, Config.OffsetY);
            settings = new GuiDialogTempStormHudSettings(api, this);
            // The HUD can be dragged only while the settings window is open.
            hud.CanDrag = () => settings.IsOpened();
            hud.OnDragged = SetCustomPosition;

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

            // Custom position: any of the 9 anchors plus valid offsets. Otherwise a preset
            // from the list (an unknown name falls back to LeftTop).
            EnumDialogArea anchor = ParseArea(Config.Position, HudTempStorm.Anchors);
            bool knownAnchor = anchor.ToString() == Config.Position;
            if (IsCustomPosition && knownAnchor && IsUsable(Config.OffsetX.Value) && IsUsable(Config.OffsetY.Value))
            {
                Position = anchor;
            }
            else
            {
                Config.OffsetX = null;
                Config.OffsetY = null;
                Position = ParseArea(Config.Position, Positions);
            }
            Config.ConfigVersion = 2;
        }

        static bool IsUsable(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

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

        static EnumDialogArea ParseArea(string value, EnumDialogArea[] allowed)
        {
            foreach (EnumDialogArea area in allowed)
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

        // A preset from the list: anchored to that edge/corner with the default margin.
        public void SetPosition(EnumDialogArea area)
        {
            if (Array.IndexOf(Positions, area) < 0) return;
            if (area == Position && !IsCustomPosition) return;
            Position = area;
            Config.Position = area.ToString();
            Config.OffsetX = null;
            Config.OffsetY = null;
            SaveConfig();
            hud.SetPlacement(area, null, null);
        }

        // After dragging: the HUD reports the nearest anchor and its offset from it.
        void SetCustomPosition(EnumDialogArea area, double offsetX, double offsetY)
        {
            Position = area;
            Config.Position = area.ToString();
            Config.OffsetX = offsetX;
            Config.OffsetY = offsetY;
            SaveConfig();
            // Recomposed so the position list shows "Custom".
            if (settings.IsOpened()) settings.Refresh();
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
            hud.CheckLayout();
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
        // Extra width so rounding never makes the text wrap.
        const double WidthReserve = 4;
        // The box narrows only when the text gets this much shorter, so it doesn't
        // twitch (e.g. at the right edge) every time a digit of the countdown changes.
        const double ShrinkThreshold = 30;

        public override string ToggleKeyCombinationCode => null;

        // Anchors a dragged HUD can get: the 3x3 grid of screen areas, row by row.
        public static readonly EnumDialogArea[] Anchors =
        {
            EnumDialogArea.LeftTop, EnumDialogArea.CenterTop, EnumDialogArea.RightTop,
            EnumDialogArea.LeftMiddle, EnumDialogArea.CenterMiddle, EnumDialogArea.RightMiddle,
            EnumDialogArea.LeftBottom, EnumDialogArea.CenterBottom, EnumDialogArea.RightBottom
        };

        int lineCount;
        double textWidth;
        float composedScale;
        int composedFrameWidth, composedFrameHeight;
        string text = "Temporal Storm HUD";

        // Placement: anchored to a screen area, shifted by an offset in GUI units.
        EnumDialogArea area;
        double offsetX, offsetY;
        bool customPosition;

        // Dragging. Allowed only while CanDrag() is true (the settings window is open:
        // the mouse cursor is free then, and nothing gets moved by accident in normal play).
        public Func<bool> CanDrag;
        public Action<EnumDialogArea, double, double> OnDragged;
        bool dragging;
        double dragX, dragY;            // top-left corner in GUI units
        int lastMouseX, lastMouseY;

        static double Scale => RuntimeEnv.GUIScale > 0 ? RuntimeEnv.GUIScale : 1;

        public HudTempStorm(ICoreClientAPI capi, EnumDialogArea area, double? offX, double? offY) : base(capi)
        {
            SetPlacementFields(area, offX, offY);
            ComposeFor(text, 1, MeasureWidth(text));
        }

        // Null offsets = a preset position with the default margin.
        void SetPlacementFields(EnumDialogArea newArea, double? offX, double? offY)
        {
            area = newArea;
            customPosition = offX.HasValue && offY.HasValue;
            offsetX = offX ?? OffsetX(newArea);
            offsetY = offY ?? OffsetY(newArea);
        }

        // The vanilla coordinates box (HudElementCoordinates) places itself below the first
        // opened GUI aligned exactly RightTop (IGuiAPI.GetDialogBoundsInArea), wherever it is.
        // A dragged HUD anchored top-right uses RightFixed instead: the same position
        // (Y comes from the offset alone), but the coordinates box no longer follows it.
        // The "Top right" preset keeps RightTop, so with the minimap off the coordinates
        // box stacks below the HUD instead of overlapping it.
        static EnumDialogArea LayoutAlignment(EnumDialogArea anchor, bool custom)
        {
            return custom && anchor == EnumDialogArea.RightTop ? EnumDialogArea.RightFixed : anchor;
        }

        public void SetPlacement(EnumDialogArea newArea, double? offX, double? offY)
        {
            SetPlacementFields(newArea, offX, offY);
            ComposeFor(text, lineCount, textWidth);
        }

        // Text width doesn't scale exactly with the GUI scale (e.g. at a smaller scale
        // the text takes relatively more room and wrapped), and the HUD must stay on screen
        // when the window gets smaller. The API has no event for either, so this is called
        // on every tick and rebuilds the HUD when the scale or the window size changed.
        public void CheckLayout()
        {
            if (dragging) return;
            if (RuntimeEnv.GUIScale != composedScale
                || capi.Render.FrameWidth != composedFrameWidth
                || capi.Render.FrameHeight != composedFrameHeight)
            {
                ComposeFor(text, lineCount, MeasureWidth(text));
            }
        }

        // Mouse handling follows the game's own dialog dragging (GuiElementDialogTitleBar):
        // switch the bounds to a fixed position, move it by the mouse delta / GUI scale.
        public override void OnMouseDown(MouseEvent args)
        {
            if (!args.Handled && args.Button == EnumMouseButton.Left && CanDrag != null && CanDrag()
                && SingleComposer != null && SingleComposer.Bounds.PointInside(args.X, args.Y))
            {
                ElementBounds b = SingleComposer.Bounds;
                dragging = true;
                dragX = b.absX / Scale;
                dragY = b.absY / Scale;
                lastMouseX = args.X;
                lastMouseY = args.Y;
                ApplyDragPosition();
                args.Handled = true;
                return;
            }
            base.OnMouseDown(args);
        }

        public override void OnMouseMove(MouseEvent args)
        {
            if (dragging)
            {
                dragX += (args.X - lastMouseX) / Scale;
                dragY += (args.Y - lastMouseY) / Scale;
                lastMouseX = args.X;
                lastMouseY = args.Y;
                ApplyDragPosition();
                args.Handled = true;
                return;
            }
            base.OnMouseMove(args);
        }

        public override void OnMouseUp(MouseEvent args)
        {
            if (dragging)
            {
                dragging = false;
                args.Handled = true;
                FinishDrag();
                return;
            }
            base.OnMouseUp(args);
        }

        // Puts the HUD's top-left corner at dragX/dragY, kept inside the window.
        void ApplyDragPosition()
        {
            ElementBounds b = SingleComposer.Bounds;
            ElementBounds window = capi.Gui.WindowBounds;
            dragX = Math.Clamp(dragX, 0, Math.Max(0, (window.InnerWidth - b.OuterWidth) / Scale));
            dragY = Math.Clamp(dragY, 0, Math.Max(0, (window.InnerHeight - b.OuterHeight) / Scale));

            b.Alignment = EnumDialogArea.None;
            b.fixedOffsetX = 0;
            b.fixedOffsetY = 0;
            b.absMarginX = 0;
            b.absMarginY = 0;
            b.fixedX = dragX;
            b.fixedY = dragY;
            b.MarkDirtyRecursive();
            b.CalcWorldBounds();
        }

        // Anchors the dropped HUD to the screen area (3x3 grid) its center is in and stores
        // the offset from that anchor. A HUD near the right edge then stays at that edge when
        // the window is resized, and grows to the left when its text gets longer.
        void FinishDrag()
        {
            ElementBounds b = SingleComposer.Bounds;
            ElementBounds window = capi.Gui.WindowBounds;
            double x = b.absX;
            double y = b.absY;

            int col = GridIndex(x + b.OuterWidth / 2, window.InnerWidth);
            int row = GridIndex(y + b.OuterHeight / 2, window.InnerHeight);
            EnumDialogArea anchor = Anchors[row * 3 + col];

            // Where the game puts the HUD for this anchor with no offset.
            b.Alignment = LayoutAlignment(anchor, true);
            b.fixedX = 0;
            b.fixedY = 0;
            b.fixedOffsetX = 0;
            b.fixedOffsetY = 0;
            b.absMarginX = 0;
            b.absMarginY = 0;
            b.MarkDirtyRecursive();
            b.CalcWorldBounds();

            double offX = Math.Round((x - b.absX) / Scale);
            double offY = Math.Round((y - b.absY) / Scale);

            SetPlacement(anchor, offX, offY);
            OnDragged?.Invoke(anchor, offX, offY);
        }

        static int GridIndex(double center, double size)
        {
            if (center < size / 3) return 0;
            if (center > size * 2 / 3) return 2;
            return 1;
        }

        // Shifts the HUD back inside the window if it sticks out (e.g. a custom position
        // after the window got smaller). Only for display: the stored position is kept,
        // so the HUD returns to it when the window is big enough again.
        void KeepOnScreen()
        {
            ElementBounds b = SingleComposer.Bounds;
            ElementBounds window = capi.Gui.WindowBounds;
            if (window.InnerWidth <= 0 || window.InnerHeight <= 0) return; // e.g. minimized window
            double dx = 0;
            double dy = 0;

            if (b.absX + b.OuterWidth > window.InnerWidth) dx = window.InnerWidth - b.OuterWidth - b.absX;
            if (b.absX + dx < 0) dx = -b.absX;
            if (b.absY + b.OuterHeight > window.InnerHeight) dy = window.InnerHeight - b.OuterHeight - b.absY;
            if (b.absY + dy < 0) dy = -b.absY;

            if (dx == 0 && dy == 0) return;
            b.fixedOffsetX += dx / Scale;
            b.fixedOffsetY += dy / Scale;
            b.CalcWorldBounds();
        }

        // Width (in GUI units) the text needs without wrapping. Lays out each line
        // with the game's own richtext components, the same way GuiElementRichtext does
        // (CalcHeightAndPositions), but in a practically unlimited width.
        double MeasureWidth(string vtml)
        {
            CairoFont font = CairoFont.WhiteSmallText();
            TextFlowPath[] flowPath = { new TextFlowPath(100000) };
            double maxX = 0;

            foreach (string line in vtml.Split("<br>"))
            {
                if (line.Length == 0) continue;

                RichTextComponentBase[] comps = VtmlUtil.Richtextify(capi, line, font);
                double posX = 0;
                foreach (RichTextComponentBase comp in comps)
                {
                    comp.CalcBounds(flowPath, 0, posX, 0, out double nextPosX);
                    if (comp.Float == EnumFloat.Inline) posX = nextPosX;

                    if (comp.BoundsPerLine != null)
                    {
                        foreach (LineRectangled b in comp.BoundsPerLine) maxX = Math.Max(maxX, b.X + b.Width);
                    }
                    comp.Dispose();
                }
            }

            // Component bounds are in screen pixels; ElementBounds.Fixed takes GUI units.
            double scale = RuntimeEnv.GUIScale > 0 ? RuntimeEnv.GUIScale : 1;
            return Math.Ceiling(maxX / scale) + WidthReserve;
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

        // The background only fits the text when composing, so the HUD is rebuilt
        // whenever the number of lines, the needed width or the position changes.
        void ComposeFor(string newText, int lines, double width)
        {
            text = newText;
            lineCount = lines;
            textWidth = width;
            composedScale = RuntimeEnv.GUIScale;
            composedFrameWidth = capi.Render.FrameWidth;
            composedFrameHeight = capi.Render.FrameHeight;
            SingleComposer?.Dispose();

            ElementBounds textBounds = ElementBounds.Fixed(0, 0, width, 20 * lines);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(8);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(textBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(LayoutAlignment(area, customPosition))
                .WithFixedAlignmentOffset(offsetX, offsetY);

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

            if (dragging) ApplyDragPosition();
            else KeepOnScreen();
        }

        public void SetText(string newText)
        {
            int lines = newText.Split("<br>").Length;
            double width = MeasureWidth(newText);
            bool resize = width > textWidth || width < textWidth - ShrinkThreshold;

            if (lines != lineCount || resize) ComposeFor(newText, lines, width);
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

        // Shows the current settings again (e.g. "Custom" after the HUD was dragged).
        public void Refresh()
        {
            if (IsOpened()) Compose();
        }

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

            y += rowHeight + rowGap;
            ElementBounds hintBounds = ElementBounds.Fixed(0, y, controlX + controlWidth, 20);

            y += 20 + rowGap + 10;
            // Left side, so the expanded position list doesn't cover it.
            ElementBounds closeBounds = ElementBounds.Fixed(0, y, 0, 0)
                .WithFixedPadding(10, 2);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;
            bgBounds.WithChildren(showLabelBounds, showSwitchBounds, positionLabelBounds, positionBounds, hintBounds, closeBounds);

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            // The presets, plus "Custom" while the HUD has a dragged position
            // (choosing a preset goes back to it with the default margin).
            EnumDialogArea[] positions = TempStormHudSystem.Positions;
            bool custom = system.IsCustomPosition;
            int count = positions.Length + (custom ? 1 : 0);
            string[] values = new string[count];
            string[] names = new string[count];
            for (int i = 0; i < positions.Length; i++)
            {
                values[i] = positions[i].ToString();
                names[i] = L("position-" + values[i].ToLowerInvariant());
            }
            int selected;
            if (custom)
            {
                values[count - 1] = "Custom";
                names[count - 1] = L("position-custom");
                selected = count - 1;
            }
            else
            {
                selected = Math.Max(0, Array.IndexOf(positions, system.Position));
            }

            SingleComposer?.Dispose();
            SingleComposer = capi.Gui.CreateCompo("tempstormhud-settings", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(L("settings-title"), () => TryClose())
                .BeginChildElements(bgBounds)
                    .AddStaticText(L("settings-show"), CairoFont.WhiteSmallText(), showLabelBounds)
                    .AddSwitch(system.SetShowHud, showSwitchBounds, "showhud")
                    .AddStaticText(L("settings-position"), CairoFont.WhiteSmallText(), positionLabelBounds)
                    .AddDropDown(values, names, selected, OnPositionChanged, positionBounds, "position")
                    .AddStaticText(L("settings-drag-hint"), CairoFont.WhiteSmallText(), hintBounds)
                    .AddSmallButton(L("settings-close"), () => TryClose(), closeBounds)
                .EndChildElements()
                .Compose();

            SingleComposer.GetSwitch("showhud").SetValue(system.Config.ShowHud);
        }

        void OnPositionChanged(string code, bool selected)
        {
            if (!selected) return;
            // "Custom" is not an EnumDialogArea name, so selecting it changes nothing.
            if (Enum.TryParse(code, out EnumDialogArea area)) system.SetPosition(area);
        }
    }
}
