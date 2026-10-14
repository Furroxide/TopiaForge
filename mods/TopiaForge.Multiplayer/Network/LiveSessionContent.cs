using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// What a live session needs from the runtime about installed content: the packages a host announces, what a joining
    /// player presents, the host's admission decision, and the player's readiness for a host's descriptor. The runtime
    /// supplies the real one through its internal service interface (WS3); until then <see cref="ProviderOnlyContent"/>
    /// stands in.
    /// </summary>
    internal interface ILiveSessionContent
    {
        /// <summary>Gets the Robotopia build this process runs.</summary>
        string GameBuild { get; }

        /// <summary>Gets the TopiaForge runtime version.</summary>
        string RuntimeVersion { get; }

        /// <summary>Gets the packages a host lists in its descriptor, sorted by id.</summary>
        IReadOnlyList<DescriptorPackage> HostPackages();

        /// <summary>Gets what a joining player presents for admission, as opaque bytes the host's runtime reads.</summary>
        byte[] ClientContent();

        /// <summary>
        /// Decides, on the host, whether a player presenting <paramref name="clientContent"/> may join. A refusal lists
        /// what the player is missing or runs differently; the host's policy turns it into the admission report.
        /// </summary>
        ContentAdmission Admit(byte[] clientContent);

        /// <summary>Checks, on a joining player, whether the host's packages are installed exactly.</summary>
        ContentCheck Check(ServerDescriptor descriptor);
    }

    /// <summary>
    /// The runtime's verdict on what a joining player presents: admitted, or refused with a code and one line per problem,
    /// such as "Zombies 0.2.0 is missing.". The runtime's admission report (one mismatch per package, each with its
    /// message) fills it directly. Content refuses for its packages (<see cref="AdmissionCode.Content"/>) or for a
    /// runtime protocol it cannot share (<see cref="AdmissionCode.Protocol"/>); every other code belongs to the host.
    /// </summary>
    internal sealed class ContentAdmission
    {
        internal static readonly ContentAdmission Admitted = new ContentAdmission(true, AdmissionCode.Other, Array.Empty<string>());

        private ContentAdmission(bool admitted, AdmissionCode code, IReadOnlyList<string> problems)
        {
            IsAdmitted = admitted;
            Code = code;
            Problems = problems;
        }

        internal bool IsAdmitted { get; }

        /// <summary>Gets why the content was refused: <see cref="AdmissionCode.Content"/> or <see cref="AdmissionCode.Protocol"/>.</summary>
        internal AdmissionCode Code { get; }

        /// <summary>Gets the lines the player sees, in order; the report keeps those that are display text and fit its bound.</summary>
        internal IReadOnlyList<string> Problems { get; }

        internal static ContentAdmission Refused(IReadOnlyList<string> problems, AdmissionCode code = AdmissionCode.Content)
        {
            if (problems == null) throw new ArgumentNullException(nameof(problems));
            if (code != AdmissionCode.Content && code != AdmissionCode.Protocol)
            {
                throw new ArgumentOutOfRangeException(nameof(code), "Content refuses a join for its packages or its protocol; the host decides every other refusal.");
            }

            return new ContentAdmission(false, code, new List<string>(problems).AsReadOnly());
        }
    }

    /// <summary>
    /// The stand-in until the runtime provides content: it announces no packages, presents nothing, admits everyone and
    /// is always ready, so the engine's own protocol and contract checks are all that apply.
    /// </summary>
    internal sealed class ProviderOnlyContent : ILiveSessionContent
    {
        internal ProviderOnlyContent(string gameBuild, string runtimeVersion)
        {
            if (!ServerDescriptor.IsGameBuild(gameBuild)) throw new ArgumentException("A game build is 1 to 16 letters, digits, dots, dashes or underscores.", nameof(gameBuild));
            if (!ServerDescriptor.IsSemanticVersion(runtimeVersion)) throw new ArgumentException("The runtime version is a semantic version.", nameof(runtimeVersion));
            GameBuild = gameBuild;
            RuntimeVersion = runtimeVersion;
        }

        public string GameBuild { get; }

        public string RuntimeVersion { get; }

        public IReadOnlyList<DescriptorPackage> HostPackages() => Array.Empty<DescriptorPackage>();

        public byte[] ClientContent() => Array.Empty<byte>();

        public ContentAdmission Admit(byte[] clientContent) => ContentAdmission.Admitted;

        public ContentCheck Check(ServerDescriptor descriptor) => ContentCheck.Ready;
    }

    /// <summary>
    /// The bytes a joining player puts in its join request: version 1, a flag byte, a 32-byte password proof when the
    /// flag says so, then the runtime's content bytes with a varint length. The host's policy reads it; the engine never
    /// looks inside, but it bounds the whole field, so the content is bounded to fit with its header.
    /// </summary>
    internal static class JoinContent
    {
        private const byte Version = 1;
        private const byte HasProof = 1;
        private const int HeaderBytes = 2 + PasswordProof.ProofSize + 3;

        /// <summary>The most content a player presents: the engine's bound on the field, less the largest header.</summary>
        internal const int MaximumContentBytes = ProtocolLimits.MaximumOpaqueBytes - HeaderBytes;

        internal static byte[] Encode(byte[]? passwordProof, byte[] content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (passwordProof != null && passwordProof.Length != PasswordProof.ProofSize) throw new ArgumentException("A proof is 32 bytes.", nameof(passwordProof));
            if (content.Length > MaximumContentBytes) throw new ArgumentException("Join content must fit the engine's 64 KiB field with its header.", nameof(content));
            var output = new List<byte>(2 + (passwordProof?.Length ?? 0) + 5 + content.Length) { Version, passwordProof != null ? HasProof : (byte)0 };
            if (passwordProof != null) output.AddRange(passwordProof);
            var length = (uint)content.Length;
            do
            {
                var next = (byte)(length & 0x7F);
                length >>= 7;
                output.Add(length != 0 ? (byte)(next | 0x80) : next);
            }
            while (length != 0);
            output.AddRange(content);
            return output.ToArray();
        }

        /// <summary>Reads join content; false for anything malformed. An empty value is a player with no proof or content.</summary>
        internal static bool TryDecode(byte[] bytes, out byte[]? passwordProof, out byte[] content)
        {
            passwordProof = null;
            content = Array.Empty<byte>();
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length == 0) return true;
            if (bytes.Length < 3 || bytes[0] != Version || (bytes[1] & ~HasProof) != 0) return false;
            var position = 2;
            if ((bytes[1] & HasProof) != 0)
            {
                if (bytes.Length < position + PasswordProof.ProofSize + 1) return false;
                passwordProof = new byte[PasswordProof.ProofSize];
                Buffer.BlockCopy(bytes, position, passwordProof, 0, PasswordProof.ProofSize);
                position += PasswordProof.ProofSize;
            }

            // Five varint bytes carry 35 bits, so the length is read into 64: a length past the bound is refused however
            // it is spelled, rather than wrapping in 32 bits to a small one under a second spelling.
            ulong length = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (shift > 28 || position >= bytes.Length) return false;
                var next = bytes[position++];
                length |= (ulong)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    if (next == 0 && shift > 0) return false;
                    break;
                }
            }

            if (length > MaximumContentBytes || (ulong)(bytes.Length - position) != length) return false;
            content = new byte[length];
            Buffer.BlockCopy(bytes, position, content, 0, (int)length);
            return true;
        }
    }

    internal enum AdmissionCode
    {
        Other = 0,
        Full = 1,
        Protocol = 2,
        PasswordRequired = 3,
        PasswordWrong = 4,
        TooManyAttempts = 5,
        Banned = 6,
        Content = 7
    }

    /// <summary>
    /// Why a host refused a join, as canonical JSON in the rejection's report: <c>{"code":"...","schema":
    /// "topiaforge.admission-report/1","problems":[...]}</c>. The joining player's interface reads the code; the problems
    /// list a content refusal's packages. It never names an address.
    /// </summary>
    internal static class AdmissionReport
    {
        internal const string Schema = "topiaforge.admission-report/1";
        internal const int MaximumBytes = 16 * 1024;
        private const int MaximumProblems = 64;

        private static readonly string[] Codes = { "other", "full", "protocol", "password-required", "password-wrong", "too-many-attempts", "banned", "content" };

        /// <summary>Encodes a report, keeping problems in order until the next would not fit the reader's bound.</summary>
        internal static byte[] Encode(AdmissionCode code, IReadOnlyList<string>? problems = null)
        {
            var items = new List<JsonNode>();
            var encoded = Serialize(code, items);
            if (problems == null) return encoded;
            for (var index = 0; index < problems.Count && items.Count < MaximumProblems; index++)
            {
                var problem = problems[index];
                if (!WireText.IsDisplayText(problem, 256)) continue;
                items.Add(new JsonStringNode(problem));
                var larger = Serialize(code, items);
                if (larger.Length > MaximumBytes)
                {
                    items.RemoveAt(items.Count - 1);
                    break;
                }

                encoded = larger;
            }

            return encoded;
        }

        private static byte[] Serialize(AdmissionCode code, List<JsonNode> problems) =>
            CanonicalJson.Serialize(new JsonObjectNode().Add("schema", Schema).Add("code", Codes[(int)code]).Add("problems", new JsonArrayNode(new List<JsonNode>(problems))));

        internal static bool TryDecode(byte[] bytes, out AdmissionCode code, out IReadOnlyList<string> problems)
        {
            code = AdmissionCode.Other;
            problems = Array.Empty<string>();
            if (bytes == null || bytes.Length == 0 || !CanonicalJson.TryParse(bytes, MaximumBytes, out var node, out _)) return false;
            if (!(node is JsonObjectNode value) || !value.HasExactly("schema", "code", "problems")) return false;
            if (!value.TryGet("schema", out var schema) || !(schema is JsonStringNode schemaText) || schemaText.Value != Schema) return false;
            if (!value.TryGet("code", out var codeNode) || !(codeNode is JsonStringNode codeText)) return false;
            var index = Array.IndexOf(Codes, codeText.Value);
            if (index < 0 || !value.TryGet("problems", out var list) || !(list is JsonArrayNode array) || array.Items.Count > MaximumProblems) return false;
            var read = new List<string>(array.Items.Count);
            foreach (var item in array.Items)
            {
                if (!(item is JsonStringNode text) || !WireText.IsDisplayText(text.Value, 256)) return false;
                read.Add(text.Value);
            }

            code = (AdmissionCode)index;
            problems = read.AsReadOnly();
            return true;
        }
    }
}
