using System;
using System.Text;

namespace TopiaForge.Multiplayer.Network
{
    internal enum DescriptorRequestKind
    {
        /// <summary>No newline has arrived yet, and the request still has room.</summary>
        Incomplete = 0,

        /// <summary>The request filled its 128 bytes without a newline.</summary>
        Overlong = 1,

        /// <summary>The line is neither request.</summary>
        Malformed = 2,

        /// <summary><c>TFD1 descriptor</c></summary>
        Descriptor = 3,

        /// <summary><c>TFD1 package &lt;sha256&gt;</c></summary>
        Package = 4
    }

    /// <summary>
    /// A descriptor endpoint request, read from the bytes a connection has sent so far. The request is the line before
    /// the first newline, within the first 128 bytes; only <c>TFD1 descriptor</c> and <c>TFD1 package</c> with a
    /// lowercase SHA-256 are requests, and any other line, control characters and all, is malformed.
    /// </summary>
    internal readonly struct DescriptorRequest
    {
        internal const int MaximumBytes = 128;
        private const string DescriptorLine = "TFD1 descriptor";
        private const string PackagePrefix = "TFD1 package ";

        private DescriptorRequest(DescriptorRequestKind kind, string? sha256)
        {
            Kind = kind;
            Sha256 = sha256;
        }

        internal DescriptorRequestKind Kind { get; }

        /// <summary>Gets the package a <see cref="DescriptorRequestKind.Package"/> request names.</summary>
        internal string? Sha256 { get; }

        /// <summary>Reads the first <paramref name="length"/> bytes of <paramref name="buffer"/>; nothing a peer sends makes it throw.</summary>
        internal static DescriptorRequest Read(byte[] buffer, int length)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (length < 0 || length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(length));
            var end = Array.IndexOf(buffer, (byte)'\n', 0, Math.Min(length, MaximumBytes));
            if (end < 0) return new DescriptorRequest(length >= MaximumBytes ? DescriptorRequestKind.Overlong : DescriptorRequestKind.Incomplete, null);
            var line = Encoding.ASCII.GetString(buffer, 0, end);
            if (line == DescriptorLine) return new DescriptorRequest(DescriptorRequestKind.Descriptor, null);
            if (line.StartsWith(PackagePrefix, StringComparison.Ordinal) && WireText.IsHex(line.Substring(PackagePrefix.Length), 32))
            {
                return new DescriptorRequest(DescriptorRequestKind.Package, line.Substring(PackagePrefix.Length));
            }

            return new DescriptorRequest(DescriptorRequestKind.Malformed, null);
        }
    }
}
