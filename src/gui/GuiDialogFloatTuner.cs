using System;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace UniversalDisplayLib
{
    /// <summary>
    /// The levitating display's menu: sneak + right-click a floating item, its base, or the standalone block with an
    /// empty hand. Built like Taxidermy's adjust window - a labelled number field per value, Reset and Close - and
    /// opened by the server once it has checked the claim (FloatMenuOpenPacket).
    ///
    /// Live: every change goes to the server (FloatTunePacket), which stores it on the block for this spot and sends it
    /// back to every player through the block's normal sync. Works the same on the standalone block and on a base stood
    /// on any display - windows, shelves, ground storage.
    /// </summary>
    public sealed class GuiDialogFloatTuner : GuiDialog
    {
        public override string ToggleKeyCombinationCode => null;
        public override bool PrefersUngrabbedMouse => true;

        private readonly BEUniversalDisplay be;
        private readonly string spotId;
        private float rise, offsetX, offsetZ, tilt, tiltForward, rot, spin, bob, bobSeconds;
        private bool filling;

        /// <summary>Field key, lang key, arrow step.</summary>
        private static readonly (string Key, string Label, float Step)[] Fields =
        {
            ("rise", "universaldisplaylib:levitatemenu-height", 0.5f),
            ("offsetx", "universaldisplaylib:levitatemenu-offsetx", 0.25f),
            ("offsetz", "universaldisplaylib:levitatemenu-offsetz", 0.25f),
            ("tilt", "universaldisplaylib:levitatemenu-tilt", 5f),
            ("tiltforward", "universaldisplaylib:levitatemenu-tiltforward", 5f),
            ("rot", "universaldisplaylib:levitatemenu-rotation", 15f),
            ("spin", "universaldisplaylib:levitatemenu-spin", 1f),
            ("bob", "universaldisplaylib:levitatemenu-bob", 0.1f),
            ("bobseconds", "universaldisplaylib:levitatemenu-bobseconds", 0.5f),
        };

        public GuiDialogFloatTuner(ICoreClientAPI capi, BEUniversalDisplay be, string spotId) : base(capi)
        {
            this.be = be;
            this.spotId = spotId;
            Load(be.TuningAt(spotId));
            Compose();
        }

        private void Load(FloatTuning t)
        {
            rise = t.Rise; offsetX = t.OffsetX; offsetZ = t.OffsetZ; tilt = t.Tilt; tiltForward = t.TiltForward; rot = t.Rot; spin = t.Spin; bob = t.Bob; bobSeconds = t.BobSeconds;
        }

        private const int W = 320, LabelW = 130, FieldW = 190, RowH = 30, LabelH = 22;
        private static ElementBounds Row(int x, int y, int w, int h) => ElementBounds.Fixed(x, y, w, h);

        private void Compose()
        {
            ClearComposers();
            ElementBounds bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bg.BothSizing = ElementSizing.FitToChildren;
            ElementBounds dialog = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);

            var c = capi.Gui.CreateCompo("universaldisplaylib-floatmenu", dialog)
                .AddShadedDialogBG(bg)
                .AddDialogTitleBar(Lang.Get("universaldisplaylib:levitatemenu-title"), () => TryClose())
                .BeginChildElements(bg);

            int y = 32;
            foreach (var (key, label, _) in Fields)
            {
                c.AddStaticText(Lang.Get(label), CairoFont.WhiteDetailText(), Row(0, y + 4, LabelW, LabelH));
                string k = key;
                c.AddNumberInput(Row(LabelW, y, FieldW, RowH), v => OnNumber(k, v), CairoFont.WhiteDetailText(), key);
                y += RowH + 6;
            }
            y += 4;
            c.AddSmallButton(Lang.Get("universaldisplaylib:levitatemenu-reset"), OnReset, Row(0, y, 120, 26));
            c.AddSmallButton(Lang.Get("universaldisplaylib:levitatemenu-close"), () => TryClose(), Row(W - 120, y, 120, 26));

            SingleComposer = c.EndChildElements().Compose();
            Fill();
        }

        private void Fill()
        {
            filling = true;
            foreach (var (key, _, step) in Fields) Set(key, Value(key), step);
            filling = false;
        }

        private float Value(string key) => key switch
        {
            "rise" => rise,
            "offsetx" => offsetX,
            "offsetz" => offsetZ,
            "tilt" => tilt,
            "tiltforward" => tiltForward,
            "rot" => rot,
            "spin" => spin,
            "bob" => bob,
            _ => bobSeconds
        };

        /// <summary>IntMode off and an explicit step per field, as Taxidermy's window and the transform editor do.</summary>
        private void Set(string key, float value, float step)
        {
            var el = SingleComposer?.GetNumberInput(key);
            if (el == null) return;
            el.IntMode = false;
            el.Interval = step;
            el.SetValue((float)Math.Round(value, 2));
        }

        private void OnNumber(string key, string text)
        {
            if (filling) return;
            // "NaN" and "Infinity" parse as numbers too
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || !float.IsFinite(v)) return;
            switch (key)
            {
                case "rise": rise = v; break;
                case "offsetx": offsetX = v; break;
                case "offsetz": offsetZ = v; break;
                case "tilt": tilt = v; break;
                case "tiltforward": tiltForward = v; break;
                case "rot": rot = v; break;
                case "spin": spin = v; break;
                case "bob": bob = v; break;
                case "bobseconds": bobSeconds = v; break;
            }
            Send(false);
        }

        /// <summary>Back to the base's own settings (its block's JSON).</summary>
        private bool OnReset()
        {
            Send(true);
            Load(be.DefaultTuningAt(spotId));
            Fill();
            return true;
        }

        private void Send(bool reset)
        {
            UniversalDisplayLibMod.ClientChannel?.SendPacket(new FloatTunePacket
            {
                X = be.Pos.X, Y = be.Pos.Y, Z = be.Pos.Z, SpotId = spotId, Reset = reset,
                Rise = rise, Tilt = tilt, TiltForward = tiltForward, Rot = rot, Spin = spin, Bob = bob, BobSeconds = bobSeconds,
                OffsetX = offsetX, OffsetZ = offsetZ
            });
        }
    }
}
