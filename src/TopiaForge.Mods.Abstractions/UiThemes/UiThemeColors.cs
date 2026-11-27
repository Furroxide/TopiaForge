using System;

namespace TopiaForge.Mods
{
    internal readonly struct UiThemeColor : IEquatable<UiThemeColor>
    {
        internal UiThemeColor(byte red, byte green, byte blue)
        {
            R = red;
            G = green;
            B = blue;
        }

        internal byte R { get; }
        internal byte G { get; }
        internal byte B { get; }

        internal static UiThemeColor Parse(string value, string path)
        {
            if (value.Length != 7 || value[0] != '#')
            {
                throw UiThemeDataException.Invalid(path, "Expected an opaque #RRGGBB color.");
            }

            Span<byte> channels = stackalloc byte[3];
            for (var channel = 0; channel < channels.Length; channel++)
            {
                var high = UiThemeData.HexDigit(value[(2 * channel) + 1]);
                var low = UiThemeData.HexDigit(value[(2 * channel) + 2]);
                if (high < 0 || low < 0)
                {
                    throw UiThemeDataException.Invalid(path, "Expected an opaque #RRGGBB color.");
                }

                channels[channel] = (byte)((high << 4) | low);
            }

            return new UiThemeColor(channels[0], channels[1], channels[2]);
        }

        internal UiThemeColor Tinted(float tint) => new UiThemeColor(
            TintChannel(R, tint), TintChannel(G, tint), TintChannel(B, tint));

        private static byte TintChannel(byte value, float tint) =>
            (byte)Math.Round(Math.Clamp((value / 255f) * tint, 0f, 1f) * 255f);

        public bool Equals(UiThemeColor other) => R == other.R && G == other.G && B == other.B;
        public override bool Equals(object? obj) => obj is UiThemeColor color && Equals(color);
        public override int GetHashCode() => (R << 16) | (G << 8) | B;
        public override string ToString() => "#" + UiThemeData.Hex(stackalloc byte[] { R, G, B });
    }

    internal readonly struct UiThemePair
    {
        internal UiThemePair(UiThemeColor fill, UiThemeColor on)
        {
            Fill = fill;
            On = on;
        }

        internal UiThemeColor Fill { get; }
        internal UiThemeColor On { get; }
    }

    internal readonly struct UiThemeStateTints
    {
        internal UiThemeStateTints(float hovered, float selected, float pressed)
        {
            Hovered = hovered;
            Selected = selected;
            Pressed = pressed;
        }

        internal float Hovered { get; }
        internal float Selected { get; }
        internal float Pressed { get; }
    }

    internal static class UiThemeColors
    {
        internal const float ButtonHovered = 1.06f;
        internal const float ButtonSelected = 1.04f;
        internal const float ButtonPressed = 0.94f;
        internal const float ControlHighlighted = 245f / 255f;
        internal const float ControlPressed = 200f / 255f;

        private static readonly UiThemeColor Black = new UiThemeColor(0, 0, 0);
        private static readonly UiThemeColor White = new UiThemeColor(255, 255, 255);
        private static readonly double[] ChannelLuminances = CreateChannelLuminances();

        private static double[] CreateChannelLuminances()
        {
            var values = new double[256];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = Linearize(index / 255d);
            }

            return values;
        }

        internal static double Contrast(UiThemeColor left, UiThemeColor right) =>
            Ratio(Luminance(left), Luminance(right));

        private static double Ratio(double left, double right) =>
            (Math.Max(left, right) + 0.05d) / (Math.Min(left, right) + 0.05d);

        private static double Luminance(UiThemeColor color) =>
            Luminance(ChannelLuminances[color.R], ChannelLuminances[color.G], ChannelLuminances[color.B]);

        private static double Luminance(double red, double green, double blue) =>
            (0.2126d * red) + (0.7152d * green) + (0.0722d * blue);

        private static double Linearize(double channel) => channel <= 0.04045d
            ? channel / 12.92d
            : Math.Pow((channel + 0.055d) / 1.055d, 2.4d);

        internal static UiThemePair HighContrast(UiThemePair original)
        {
            if (Contrast(original.Fill, original.On) >= 7d)
            {
                return original;
            }

            var content = MoreReadableExtreme(original.Fill);
            var fill = RepairToward(original.Fill, content, content.Equals(Black) ? White : Black, 7d);
            return new UiThemePair(fill, content);
        }

        internal static UiThemeColor RepairForeground(UiThemeColor candidate, UiThemeColor fill, double minimum) =>
            RepairToward(candidate, fill, MoreReadableExtreme(fill), minimum);

        private static UiThemeColor MoreReadableExtreme(UiThemeColor background) =>
            Contrast(background, Black) >= Contrast(background, White) ? Black : White;

        private static UiThemeColor RepairToward(UiThemeColor color, UiThemeColor background, UiThemeColor target, double minimum)
        {
            if (Contrast(color, background) >= minimum)
            {
                return color;
            }

            // All channels move in the same direction toward the selected black or white endpoint.
            // The first channel-quantized interpolation step meeting the minimum is reproducible.
            for (var step = 1; step < 255; step++)
            {
                var result = new UiThemeColor(
                    Interpolate(color.R, target.R, step),
                    Interpolate(color.G, target.G, step),
                    Interpolate(color.B, target.B, step));
                if (Contrast(result, background) >= minimum)
                {
                    return result;
                }
            }

            return target;
        }

        private static byte Interpolate(byte start, byte end, int step) =>
            (byte)((((255 - step) * start) + (step * end) + 127) / 255);

        internal static UiThemeStateTints ResolveTints(UiThemePair pair, float minimum, float hovered, float selected, float pressed)
        {
            var hover = ReadableTint(pair, minimum, hovered);
            var press = ReadableTint(pair, minimum, pressed);
            var select = ReadableTint(pair, minimum, hover == hovered ? selected : Mirror(selected));
            return new UiThemeStateTints(hover, select, Beyond(pair, minimum, hover, press));
        }

        internal static double ContrastAsDrawn(UiThemePair pair, float tint)
        {
            // Check both the multiplied sRGB values and the 8-bit stored result, matching the kit's uGUI tint rule.
            var red = Linearize(Math.Clamp((pair.Fill.R / 255f) * tint, 0f, 1f));
            var green = Linearize(Math.Clamp((pair.Fill.G / 255f) * tint, 0f, 1f));
            var blue = Linearize(Math.Clamp((pair.Fill.B / 255f) * tint, 0f, 1f));
            var exact = Ratio(Luminance(pair.On), Luminance(red, green, blue));
            return Math.Min(exact, Contrast(pair.On, pair.Fill.Tinted(tint)));
        }

        private static float ReadableTint(UiThemePair pair, float minimum, float tint)
        {
            if (ContrastAsDrawn(pair, tint) >= minimum)
            {
                return tint;
            }

            var mirror = Mirror(tint);
            return ContrastAsDrawn(pair, mirror) >= minimum ? mirror : 1f;
        }

        private static float Mirror(float tint) => (float)Math.Round(2d - tint, 4, MidpointRounding.ToEven);

        private static float Beyond(UiThemePair pair, float minimum, float hover, float press)
        {
            const float tolerance = 1e-4f;
            var sameSide = (hover - 1f) * (press - 1f) > 0f;
            if (!sameSide || Math.Abs(press - 1f) > Math.Abs(hover - 1f) + tolerance)
            {
                return press;
            }

            var further = (float)Math.Round(1d + (2d * (hover - 1d)), 4, MidpointRounding.ToEven);
            return ContrastAsDrawn(pair, further) >= minimum ? further : press;
        }
    }
}
