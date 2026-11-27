using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer
{
    /// <summary>Chat moderation when the provider was started without a voice codec. Identity-bound until hosting ends.</summary>
    internal sealed partial class InternalControl
    {
        private readonly HashSet<string> chatMutes = new HashSet<string>(StringComparer.Ordinal);
        private object? chatMuteHost;

        private void RefreshChatMutes()
        {
            var host = live?.Host;
            if (ReferenceEquals(chatMuteHost, host)) return;
            chatMuteHost = host;
            chatMutes.Clear();
        }

        private OperationResult<bool> SetChatMute(ParticipantId participant, bool muted)
        {
            RefreshChatMutes();
            var identity = engine.ParticipantIdentityOf(participant);
            if (identity == null) return Fail(ModErrorCode.NotFound, "No player in this session has that id.");
            if (muted)
            {
                if (chatMutes.Count >= 1024 && !chatMutes.Contains(identity))
                    return Fail(ModErrorCode.RateLimited, "The mute list is full. End this session before starting a new one.");
                chatMutes.Add(identity);
            }
            else chatMutes.Remove(identity);
            return OperationResult<bool>.Success(true);
        }

        private bool IsChatMuted(ParticipantId participant)
        {
            RefreshChatMutes();
            var identity = engine.ParticipantIdentityOf(participant);
            return identity != null && chatMutes.Contains(identity);
        }

        private int ChatMuteCount() { RefreshChatMutes(); return chatMutes.Count; }
    }
}
