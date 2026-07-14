using System;
using System.Collections.Generic;
using System.Text;

namespace Robotopia.ModManager.Core
{
    /// <summary>
    /// Platform-independent package-path policy shared by manifest contracts and ZIP extraction. Paths remain
    /// NFC so manifest references match extracted names byte-for-byte; collision keys additionally use NFKC and
    /// invariant uppercase to catch practical Unicode/case aliases across Windows, macOS, and Linux filesystems.
    /// </summary>
    internal static class PortablePackagePath
    {
        private const int MaxPathChars = 1024;
        private const int MaxSegmentChars = 255;
        private static readonly HashSet<string> WindowsDeviceNames = new HashSet<string>(
            new[]
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            },
            StringComparer.Ordinal);

        public static bool TryValidate(
            string? path,
            out string portablePath,
            out string collisionKey,
            out string error)
        {
            portablePath = string.Empty;
            collisionKey = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(path) || path!.IndexOf('\0') >= 0)
            {
                error = "path is empty or contains a null character";
                return false;
            }

            if (path.Length > MaxPathChars)
            {
                error = "path exceeds the portable " + MaxPathChars + " character limit";
                return false;
            }

            if (path.IndexOf('\\') >= 0 || path.StartsWith("/", StringComparison.Ordinal) ||
                (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':'))
            {
                error = "path must be a portable relative path using forward slashes";
                return false;
            }

            var segments = path.Split('/');
            var canonical = new string[segments.Length];
            var folded = new string[segments.Length];
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                if (!segment.IsNormalized(NormalizationForm.FormC))
                {
                    error = "path must use Unicode NFC normalization";
                    return false;
                }

                if (IsUnsafeSegment(segment))
                {
                    error = "path contains an unsafe or non-portable segment";
                    return false;
                }

                string keySegment;
                try
                {
                    keySegment = FoldCompatibilityCase(segment);
                }
                catch (ArgumentException)
                {
                    error = "path contains invalid Unicode";
                    return false;
                }

                // Compatibility normalization can introduce reserved ASCII (for example a full-width colon or
                // slash). Reject it instead of allowing a name whose behavior changes by filesystem/runtime.
                if (keySegment.IndexOf('/') >= 0 || keySegment.IndexOf('\\') >= 0 || IsUnsafeSegment(keySegment))
                {
                    error = "path becomes unsafe under Unicode compatibility normalization";
                    return false;
                }

                canonical[index] = segment;
                folded[index] = keySegment;
            }

            portablePath = string.Join("/", canonical);
            collisionKey = string.Join("/", folded);
            return true;
        }

        private static bool IsUnsafeSegment(string segment)
        {
            if (segment.Length == 0 || segment.Length > MaxSegmentChars || segment == "." || segment == ".." ||
                segment.IndexOf(':') >= 0 || segment.EndsWith(" ", StringComparison.Ordinal) ||
                segment.EndsWith(".", StringComparison.Ordinal))
            {
                return true;
            }

            for (var index = 0; index < segment.Length; index++)
            {
                if (char.IsControl(segment[index]))
                {
                    return true;
                }

                if (char.IsHighSurrogate(segment[index]))
                {
                    if (index + 1 >= segment.Length || !char.IsLowSurrogate(segment[index + 1]))
                    {
                        return true;
                    }

                    index++;
                }
                else if (char.IsLowSurrogate(segment[index]))
                {
                    return true;
                }
            }

            var dot = segment.IndexOf('.');
            var deviceName = (dot < 0 ? segment : segment.Substring(0, dot))
                .Normalize(NormalizationForm.FormKC)
                .ToUpperInvariant();
            return WindowsDeviceNames.Contains(deviceName);
        }

        private static string FoldCompatibilityCase(string segment)
        {
            // NFKC + invariant uppercase catches the practical filesystem aliases supported by the BCL
            // (width variants, ligatures, Kelvin sign, Greek final sigma, and ordinary case). Expand both
            // sharp-S forms as well: full Unicode case folding maps them to "ss", while simple casing on
            // some supported runtimes leaves them unchanged.
            return segment
                .Normalize(NormalizationForm.FormKC)
                .ToUpperInvariant()
                .Replace("\u00DF", "SS")
                .Replace("\u1E9E", "SS");
        }
    }
}
