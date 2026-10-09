using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TopiaForge.Acceptance.Windows;

/// Baseline arithmetic and profile validation with synthetic pixels; the only file
/// touched is a throwaway BMP under the temp directory, which the broker must not alter.
internal static class ScreenBaselineTests
{
    private static JsonElement Json(string text) => BoundedJson.Parse(Encoding.UTF8.GetBytes(text));
    private static string Entry(string scenario = "routing", int cycle = 1, string step = "open", string path = "routing/c1-open.bmp", string sha = "", string tolerance = "0.02", string masks = "[{\"x\":0,\"y\":0,\"width\":320,\"height\":40}]")
        => "{\"scenarioId\":\"" + scenario + "\",\"cycle\":" + cycle + ",\"step\":\"" + step + "\",\"path\":\"" + path + "\",\"sha256\":\"" + (sha.Length == 0 ? new string('a', 64) : sha) + "\",\"tolerance\":" + tolerance + ",\"masks\":" + masks + "}";
    private static string Profile(string? baselines)
        => "{\"schemaVersion\":1,\"kind\":\"sandbox-device-profile-v1\",\"operatorReference\":\"op\",\"reviewerEvidence\":\"ev\",\"display\":{\"width\":1920,\"height\":1080,\"dpi\":96,\"deviceName\":\"\\\\\\\\.\\\\DISPLAY1\"},\"audio\":{\"endpointId\":\"e\",\"loopbackEnabled\":true},\"persistenceFiles\":[]" + (baselines == null ? "" : ",\"screenBaselines\":" + baselines) + "}";
    private static string Set(string root, params string[] entries) => "{\"root\":" + JsonSerializer.Serialize(root) + ",\"entries\":[" + string.Join(",", entries) + "]}";
    internal static void Run(Action<bool, string> assert, Action<Action, string> refuse)
    {
        const int width = 4, height = 4;
        var capture = new byte[width * height * 4];
        var baseline = new byte[width * height * 4];
        baseline[0] = 255;             // (0,0): blue differs
        baseline[15 * 4 + 2] = 200;    // (3,3): red differs
        baseline[5 * 4 + 3] = 255;     // (1,1): alpha only, ignored
        baseline[6 * 4 + 1] = ScreenBaselineSet.ChannelThreshold;      // (2,1): exactly the threshold, not a mismatch
        baseline[7 * 4 + 1] = ScreenBaselineSet.ChannelThreshold + 1;  // (3,1): one above, a mismatch
        double Fraction(params ScreenMask[] masks) => ScreenBaselineSet.MismatchFraction(capture, baseline, width, height, masks);
        assert(Fraction() == 3.0 / 16, "unmasked mismatch fraction");
        assert(Fraction(new ScreenMask(0, 0, 1, 1)) == 2.0 / 15, "a mask removes its pixel from numerator and denominator");
        assert(Fraction(new ScreenMask(0, 0, 1, 1), new ScreenMask(2, 1, 2, 3)) == 0, "masks covering every differing pixel");
        assert(Fraction(new ScreenMask(0, 0, 2, 2), new ScreenMask(1, 1, 2, 2)) == 2.0 / 9, "overlapping masks count once");
        refuse(() => Fraction(new ScreenMask(0, 0, 4, 4)), "fully masked comparison");
        refuse(() => Fraction(new ScreenMask(3, 3, 2, 1)), "mask leaving the display");
        refuse(() => ScreenBaselineSet.MismatchFraction(capture, new byte[60], width, height, []), "dimension mismatch");
        var encoded = ScreenBaselineSet.EncodeBitmap(width, height, baseline);
        assert(ScreenBaselineSet.ParsePixels(encoded, width, height).SequenceEqual(baseline), "baseline BMP round trip");
        refuse(() => ScreenBaselineSet.ParsePixels(encoded, width, 3), "baseline dimensions differ");
        refuse(() => ScreenBaselineSet.ParsePixels(encoded[..60], width, height), "baseline truncated");
        var corrupt = (byte[])encoded.Clone(); corrupt[28] = 24;
        refuse(() => ScreenBaselineSet.ParsePixels(corrupt, width, height), "baseline bit depth differs");
        var root = Environment.CurrentDirectory;
        assert(DeviceProfile.Parse(Json(Profile(null))).ScreenBaselines == null, "profile without baselines");
        var profile = DeviceProfile.Parse(Json(Profile(Set(root, Entry(), Entry(cycle: 2, path: "routing/c2-open.bmp", masks: "[]")))));
        var set = profile.ScreenBaselines!;
        assert(set.Entries.Length == 2 && set.Find("routing", 1, "open")!.Masks.Single().Width == 320 && set.Find("routing", 2, "open")!.Masks.Length == 0 && set.Find("routing", 1, "reopen") == null, "baseline entries parsed");
        assert(Math.Abs(set.Entries[0].Tolerance - 0.02) < 1e-12, "baseline tolerance parsed");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set("baselines", Entry())))), "relative baseline root");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(Path.Combine(root, "no-such-baseline-directory"), Entry())))), "missing baseline root");
        refuse(() => DeviceProfile.Parse(Json(Profile("{\"root\":" + JsonSerializer.Serialize(root) + ",\"entries\":[],\"note\":1}"))), "unknown baseline field");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry().Replace(",\"masks\"", ",\"note\":1,\"masks\""))))), "unknown baseline entry field");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(tolerance: "1.5"))))), "tolerance above one");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(tolerance: "\"0.1\""))))), "tolerance as text");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(masks: "[{\"x\":1900,\"y\":0,\"width\":40,\"height\":40}]"))))), "mask leaves the display");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(masks: "[{\"x\":0,\"y\":0,\"width\":0,\"height\":40}]"))))), "empty mask");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(), Entry(path: "routing/other.bmp"))))), "duplicate baseline key");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(scenario: "launch"))))), "unknown baseline scenario");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(cycle: 3))))), "baseline cycle beyond the contract");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(step: "hide"))))), "baseline step outside the vocabulary");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(path: "routing/c1-open.png"))))), "non-BMP baseline");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(path: "../c1-open.bmp"))))), "baseline escaping its root");
        refuse(() => DeviceProfile.Parse(Json(Profile(Set(root, Entry(sha: "abc"))))), "baseline digest shape");
        RunFiles(assert, refuse, capture, encoded, width, height);
    }
    private static void RunFiles(Action<bool, string> assert, Action<Action, string> refuse, byte[] capture, byte[] encoded, int width, int height)
    {
        var directory = Directory.CreateTempSubdirectory("topiaforge-broker-selftest-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "open.bmp"), encoded);
            var digest = Convert.ToHexStringLower(SHA256.HashData(encoded));
            var set = ScreenBaselineSet.Read(Json(Set(directory, Entry(path: "open.bmp", sha: digest, tolerance: "0.2", masks: "[]"), Entry(step: "reopen", path: "open.bmp", sha: digest, tolerance: "0.1", masks: "[]"),
                Entry(step: "end-session", path: "open.bmp", masks: "[]"), Entry(step: "hide-f5", path: "missing.bmp", sha: digest, masks: "[]"))), width, height);
            var within = set.Compare("routing", 1, "open", capture, width, height)!;
            assert(within.Path == "open.bmp" && within.Sha256 == digest && Math.Abs(within.MismatchFraction - 3.0 / 16) < 1e-12 && within.WithinTolerance, "baseline within tolerance recorded");
            var beyond = set.Compare("routing", 1, "reopen", capture, width, height)!;
            assert(Math.Abs(beyond.MismatchFraction - 3.0 / 16) < 1e-12 && !beyond.WithinTolerance, "baseline beyond tolerance recorded, not hidden");
            assert(set.Compare("routing", 2, "open", capture, width, height) == null, "no matching baseline records null");
            refuse(() => set.Compare("routing", 1, "end-session", capture, width, height), "baseline digest mismatch refused before comparison");
            try { set.Compare("routing", 1, "hide-f5", capture, width, height); throw new InvalidOperationException("missing baseline compared"); }
            catch (IOException) { assert(true, "missing baseline file refused"); }
            assert(BoundedJson.ReadBytes(Path.Combine(directory, "open.bmp"), encoded.Length).SequenceEqual(encoded) && Directory.GetFileSystemEntries(directory).Length == 1, "broker wrote no baseline");
        }
        finally { Directory.Delete(directory, true); }
    }
}
