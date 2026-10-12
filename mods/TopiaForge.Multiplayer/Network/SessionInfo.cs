using System;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// What a host tells a browser through the transport's info query before any handshake: canonical JSON of at most
    /// 1,188 bytes, the info query's limit. It is public and unauthenticated, so it names the server and its signed
    /// descriptor by hash and must never carry a network address; a browser trusts nothing in it until the descriptor
    /// it names has been fetched and verified.
    /// </summary>
    internal sealed class SessionInfo
    {
        internal const string Schema = "topiaforge.session-info/1";
        internal const int MaximumBytes = InfoPackets.MaximumInfoBytes;

        internal SessionInfo(
            string serverId,
            string descriptorSha256,
            string name,
            string gameBuild,
            string runtimeVersion,
            string protocolVersion,
            string? gamemode,
            string? world,
            int players,
            int maxPlayers,
            bool passwordRequired,
            bool voice)
        {
            if (!WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            if (!WireText.IsHex(descriptorSha256, 32)) throw new ArgumentException("A descriptor hash is 64 lowercase hexadecimal characters.", nameof(descriptorSha256));
            if (!WireText.IsDisplayText(name, ServerDescriptor.MaximumNameLength)) throw new ArgumentException("A server name is 1 to 48 printable characters.", nameof(name));
            if (!ServerDescriptor.IsGameBuild(gameBuild)) throw new ArgumentException("A game build is 1 to 16 letters, digits, dots, dashes or underscores.", nameof(gameBuild));
            if (!ServerDescriptor.IsSemanticVersion(runtimeVersion)) throw new ArgumentException("The runtime version is a semantic version.", nameof(runtimeVersion));
            if (!ServerDescriptor.IsSemanticVersion(protocolVersion)) throw new ArgumentException("The protocol version is a semantic version.", nameof(protocolVersion));
            if (gamemode != null && !WireText.IsDisplayText(gamemode, ServerDescriptor.MaximumTargetNameLength)) throw new ArgumentException("A gamemode name is 1 to 48 printable characters.", nameof(gamemode));
            if (world != null && !WireText.IsDisplayText(world, ServerDescriptor.MaximumTargetNameLength)) throw new ArgumentException("A world name is 1 to 48 printable characters.", nameof(world));
            if (maxPlayers < ServerDescriptor.MinimumPlayers || maxPlayers > ServerDescriptor.MaximumPlayers) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            if (players < 0 || players > maxPlayers) throw new ArgumentOutOfRangeException(nameof(players));
            ServerId = serverId;
            DescriptorSha256 = descriptorSha256;
            Name = name;
            GameBuild = gameBuild;
            RuntimeVersion = runtimeVersion;
            ProtocolVersion = protocolVersion;
            Gamemode = gamemode;
            World = world;
            Players = players;
            MaxPlayers = maxPlayers;
            PasswordRequired = passwordRequired;
            Voice = voice;
        }

        internal string ServerId { get; }
        internal string DescriptorSha256 { get; }
        internal string Name { get; }
        internal string GameBuild { get; }
        internal string RuntimeVersion { get; }
        internal string ProtocolVersion { get; }
        internal string? Gamemode { get; }
        internal string? World { get; }
        internal int Players { get; }
        internal int MaxPlayers { get; }
        internal bool PasswordRequired { get; }
        internal bool Voice { get; }

        internal byte[] Encode()
        {
            var bytes = CanonicalJson.Serialize(new JsonObjectNode()
                .Add("schema", Schema)
                .Add("serverId", ServerId)
                .Add("descriptorSha256", DescriptorSha256)
                .Add("name", Name)
                .Add("gameBuild", GameBuild)
                .Add("runtimeVersion", RuntimeVersion)
                .Add("protocolVersion", ProtocolVersion)
                .Add("gamemode", Gamemode)
                .Add("world", World)
                .Add("players", Players)
                .Add("maxPlayers", MaxPlayers)
                .Add("passwordRequired", PasswordRequired)
                .Add("voice", Voice));

            // Every field is bounded, so no valid info can reach the limit; this guards the arithmetic.
            if (bytes.Length > MaximumBytes) throw new InvalidOperationException("Session info outgrew the info query's limit.");
            return bytes;
        }

        internal static bool TryDecode(byte[] bytes, out SessionInfo? info, out string error)
        {
            info = null;
            if (!CanonicalJson.TryParse(bytes, MaximumBytes, out var node, out error)) return false;
            try
            {
                if (!(node is JsonObjectNode value) || !value.HasExactly(
                        "schema", "serverId", "descriptorSha256", "name", "gameBuild", "runtimeVersion", "protocolVersion",
                        "gamemode", "world", "players", "maxPlayers", "passwordRequired", "voice"))
                {
                    throw new FormatException("Session info has exactly the version 1 members.");
                }

                if (JsonRead.String(value, "schema") != Schema) throw new FormatException("The session info schema is not " + Schema + ".");
                info = new SessionInfo(
                    JsonRead.String(value, "serverId"),
                    JsonRead.String(value, "descriptorSha256"),
                    JsonRead.String(value, "name"),
                    JsonRead.String(value, "gameBuild"),
                    JsonRead.String(value, "runtimeVersion"),
                    JsonRead.String(value, "protocolVersion"),
                    JsonRead.OptionalString(value, "gamemode"),
                    JsonRead.OptionalString(value, "world"),
                    checked((int)JsonRead.Integer(value, "players")),
                    checked((int)JsonRead.Integer(value, "maxPlayers")),
                    JsonRead.Boolean(value, "passwordRequired"),
                    JsonRead.Boolean(value, "voice"));
                error = string.Empty;
                return true;
            }
            catch (Exception exception) when (exception is FormatException || exception is ArgumentException || exception is OverflowException)
            {
                error = exception.Message;
                return false;
            }
        }
    }
}
