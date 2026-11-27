namespace TopiaForge.Mods.UnityUi
{
    internal static partial class TopiaForgeGlyphRaster
    {
        // Stroke centerlines from the generic glyph board, in 24-unit view-box coordinates.
        private static readonly Shape Info = Ring(12f, 12f, 9f,
            Path(12f, 11f, 12f, 16f), Path(12f, 7.6f, 12f, 7.9f));

        private static readonly Shape Use = Ring(12f, 12f, 2.8f,
            Path(12f, 3f, 12f, 6.4f), Path(12f, 17.6f, 12f, 21f),
            Path(3f, 12f, 6.4f, 12f), Path(17.6f, 12f, 21f, 12f));

        private static readonly Shape Talk = Lines(
            Path(4f, 5.5f, 20f, 5.5f, 20f, 15.5f, 10f, 15.5f, 5.5f, 19.5f, 5.5f, 15.5f, 4f, 15.5f, 4f, 5.5f));

        private static readonly Shape Vote = Lines(
            RoundedBox(3.5f, 13f, 20.5f, 20.5f, 1.5f),
            Path(6.5f, 13f, 6.5f, 3f, 17.5f, 3f, 17.5f, 13f),
            Path(9.5f, 8f, 11.5f, 9.4f, 14.5f, 6.3f));

        private static readonly Shape Timer = Ring(12f, 13f, 8f,
            Path(12f, 9f, 12f, 13f, 15.5f, 15f), Path(9.5f, 2f, 14.5f, 2f));

        private static readonly Shape Lock = Lines(
            RoundedBox(5f, 10.5f, 19f, 20.5f, 2f),
            Path(8f, 10.5f, 8f, 7.5f), Arc(12f, 7.5f, 4f, 180f, 360f), Path(16f, 7.5f, 16f, 10.5f));

        private static readonly Shape Map = Lines(
            Path(3f, 6.5f, 9f, 4f, 15f, 6.5f, 21f, 4f, 21f, 17.5f,
                15f, 20f, 9f, 17.5f, 3f, 20f, 3f, 6.5f),
            Path(9f, 4f, 9f, 17.5f), Path(15f, 6.5f, 15f, 20f));

        private static readonly Shape Notes = Lines(
            RoundedBox(4.5f, 3.5f, 19.5f, 20.5f, 2f),
            Path(8.7f, 8f, 15.3f, 8f), Path(8.7f, 12f, 15.3f, 12f), Path(8.7f, 16f, 12f, 16f));

        private static readonly Shape Eye = Ring(12f, 12f, 3f,
            Curve(2.5f, 12f, 5.5f, 7.5f, 8.5f, 5.5f, 12f, 5.5f),
            Curve(12f, 5.5f, 15.5f, 5.5f, 18.5f, 7.5f, 21.5f, 12f),
            Curve(21.5f, 12f, 18.5f, 16.5f, 15.5f, 18.5f, 12f, 18.5f),
            Curve(12f, 18.5f, 8.5f, 18.5f, 5.5f, 16.5f, 2.5f, 12f));

        private static readonly Shape Bell = Lines(
            Arc(12f, 11f, 6f, 180f, 360f),
            Path(6f, 11f, 6f, 16f, 4.5f, 18f, 19.5f, 18f, 18f, 16f, 18f, 11f),
            Arc(12f, 20f, 2f, 0f, 180f));

        private static readonly Shape Flag = Lines(
            Path(5f, 21f, 5f, 4f, 16f, 4f, 14f, 8f, 16f, 12f, 5f, 12f));

        private static readonly Shape Star = Lines(
            Path(12f, 3.5f, 14.6f, 8.9f, 20.5f, 9.7f, 16.2f, 13.8f, 17.2f, 19.6f,
                12f, 17f, 6.8f, 19.6f, 7.8f, 13.8f, 3.5f, 9.7f, 9.4f, 8.9f, 12f, 3.5f));

        private static readonly Shape Health = Lines(
            Curve(12f, 20f, 7.8f, 17.2f, 5f, 13.6f, 5f, 10f),
            Curve(5f, 10f, 5f, 5.7f, 9.8f, 4.5f, 12f, 7.4f),
            Curve(12f, 7.4f, 14.2f, 4.5f, 19f, 5.7f, 19f, 10f),
            Curve(19f, 10f, 19f, 13.6f, 16.2f, 17.2f, 12f, 20f));

        private static readonly Shape Shield = Lines(
            Path(5f, 11f, 5f, 6f, 12f, 3f, 19f, 6f, 19f, 11f),
            Curve(19f, 11f, 19f, 15.5f, 16f, 19f, 12f, 21f),
            Curve(12f, 21f, 8f, 19f, 5f, 15.5f, 5f, 11f));

        private static readonly Shape Energy = Lines(
            Path(13f, 2.5f, 5f, 13f, 11f, 13f, 10f, 21.5f, 18f, 11f, 12f, 11f, 13f, 2.5f));

        private static readonly Shape Settings = Ring(12f, 12f, 3f,
            Path(12f, 2.5f, 12f, 5.5f), Path(12f, 18.5f, 12f, 21.5f),
            Path(2.5f, 12f, 5.5f, 12f), Path(18.5f, 12f, 21.5f, 12f),
            Path(5.3f, 5.3f, 7.4f, 7.4f), Path(16.6f, 16.6f, 18.7f, 18.7f),
            Path(5.3f, 18.7f, 7.4f, 16.6f), Path(16.6f, 7.4f, 18.7f, 5.3f));

        private static readonly Shape Player = Ring(12f, 7.5f, 3.6f,
            Curve(4.4f, 20f, 5.1f, 12.9f, 18.9f, 12.9f, 19.6f, 20f));

        private static readonly Shape Players = Ring(7.7f, 7.6f, 3.45f,
            Arc(17.5f, 8.2f, 2.6f, 0f, 360f),
            Curve(2.7f, 19.5f, 3.3f, 12.3f, 12.7f, 12.3f, 13.6f, 19.5f),
            Curve(14.4f, 14.2f, 17.4f, 12f, 21.5f, 13.8f, 21.5f, 19.2f));

        private static readonly Shape Host = Lines(
            Path(4f, 16.9f, 3f, 8f, 8f, 12f, 12f, 6f, 16f, 12f, 21f, 8f, 20f, 16.9f, 4f, 16.9f),
            Path(4f, 20.5f, 20f, 20.5f));

        private static readonly Shape Signal = Lines(
            Path(5f, 19f, 5f, 16f), Path(10f, 19f, 10f, 13f),
            Path(15f, 19f, 15f, 10f), Path(20f, 19f, 20f, 7f));

        private static readonly Shape Volume = Lines(
            Path(4f, 9.5f, 8f, 9.5f, 13f, 5.5f, 13f, 18.5f, 8f, 14.5f, 4f, 14.5f, 4f, 9.5f),
            Arc(13.84f, 12f, 4f, -48.6f, 48.6f),
            Arc(13.9f, 12f, 7.5f, -47.2f, 47.2f));

        private static readonly Shape Leave = Lines(
            Path(14f, 4f, 19f, 4f, 19f, 20f, 14f, 20f),
            Path(10f, 8f, 6f, 12f, 10f, 16f), Path(6f, 12f, 16f, 12f));

        private static readonly Shape Play = Lines(
            Path(8f, 5f, 19f, 12f, 8f, 19f, 8f, 5f));
    }
}
