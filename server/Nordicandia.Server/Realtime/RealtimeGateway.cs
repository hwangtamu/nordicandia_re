using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Game;
using MessagePack;
using Microsoft.AspNetCore.Http;
using SharedNet.Constants;
using SharedNet.Constants.Realtime;
using SharedNet.Dto;
using SharedNet.Dto.Realtime;
using SharedNet.Dto.Realtime.Messages.Payloads;

namespace Nordicandia.Server.Realtime;

/// <summary>
/// Realtime gateway for the game clients. The Unity clients open a raw WebSocket at
/// <c>/ws?status={appearOnline}&amp;characterId={guid}</c> and exchange binary MessagePack
/// <see cref="Envelope"/> frames (RequestId + Message union). Requests carry a positive
/// RequestId and expect an Envelope with the same id; RequestId 0 is a server push.
///
/// The transport is deliberately plain MessagePack (the client calls
/// <c>MessagePackSerializer.Serialize/Deserialize</c> with default options here, unlike the
/// gRPC channel which uses LZ4BlockArray), but we mirror the compression the client used on
/// the request so either client configuration works.
/// </summary>
public static class RealtimeGateway
{
    private static readonly MessagePackSerializerOptions Plain = MessagePackSerializer.DefaultOptions;
    private static readonly MessagePackSerializerOptions Lz4 =
        MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray);

    private static readonly ConcurrentDictionary<Guid, RealtimeSession> Sessions = new();

    public static async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var token = ExtractBearer(context.Request.Headers.Authorization.ToString());
        var user = State.GameStore.Instance.GetUserByAuthToken(token);
        if (user == null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Guid? characterId = Guid.TryParse(context.Request.Query["characterId"], out var parsed) ? parsed : null;
        if (characterId is { } cid && !State.GameStore.Instance.OwnsCharacter(user.UserId, cid))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var appearOnline = !string.Equals(context.Request.Query["status"], "False", StringComparison.OrdinalIgnoreCase);

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var session = new RealtimeSession(socket, user, characterId, appearOnline);
        if (characterId is { } id) Sessions[id] = session;
        try
        {
            await session.RunAsync(context.RequestAborted);
        }
        finally
        {
            if (characterId is { } rid) Sessions.TryRemove(new KeyValuePair<Guid, RealtimeSession>(rid, session));
        }
    }

    private static string ExtractBearer(string header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : header.Trim();
    }

    internal static bool LooksCompressed(ReadOnlySpan<byte> data) =>
        data.Length > 2 && data[0] == 0x92 && data[1] is 0xc7 or 0xc8 or 0xc9;

    internal static Envelope Decode(ReadOnlyMemory<byte> data)
    {
        var options = LooksCompressed(data.Span) ? Lz4 : Plain;
        return MessagePackSerializer.Deserialize<Envelope>(data, options);
    }

    internal static byte[] Encode(Envelope envelope, bool compressed) =>
        MessagePackSerializer.Serialize(envelope, compressed ? Lz4 : Plain);

    /// <summary>Collapses a channel id such as <c>Room:Global</c> or <c>1:Global</c> to the
    /// case-insensitive room key <c>global</c>, so joins and sends match regardless of the
    /// enum/int prefix the client used.</summary>
    private static string NormalizeChannel(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return "global";
        var room = RoomOf(channelId);
        return room.Trim().ToLowerInvariant();
    }

    private static string RoomOf(string channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return "Global";
        var idx = channelId.LastIndexOf(':');
        return idx >= 0 && idx < channelId.Length - 1 ? channelId[(idx + 1)..] : channelId;
    }

    private static async Task BroadcastAsync(string normalizedChannel, Message message, CancellationToken cancellationToken)
    {
        var envelope = new Envelope { RequestId = 0, Message = message };
        var delivered = 0;
        foreach (var session in Sessions.Values)
        {
            if (!session.InChannel(normalizedChannel)) continue;
            try
            {
                await session.SendAsync(envelope, cancellationToken);
                delivered++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS] broadcast to {session.characterId} failed: {ex.Message}");
            }
        }
        Console.WriteLine($"[WS] broadcast room='{normalizedChannel}' sessions={Sessions.Count} delivered={delivered}");
    }

    /// <summary>Publishes a server-generated world milestone to every connected client.</summary>
    public static void Announce(State.GameStore.WorldAnnouncement announcement)
    {
        var now = DateTime.UtcNow;
        var message = new ChannelMessageMessage
        {
            ChannelMessage = new ChannelMessageDto
            {
                ChannelId = "Global",
                MessageId = Guid.NewGuid(),
                Type = ChannelMessageType.Chat,
                SenderType = MessageSenderType.GameModeUser,
                SenderRole = UserRole.System,
                SenderUserDisplayName = "System",
                SenderCharacterDisplayName = "System",
                Content = BuildAnnouncementContent(announcement),
                CreateTime = now,
                UpdateTime = now,
                Persistent = false,
                RoomName = "Global",
            },
        };
        Console.WriteLine($"[ANNOUNCE] {announcement.Kind} {announcement.CharacterName} mode={announcement.GameMode} tier={announcement.WorldTier}-{announcement.WorldWaypoint}");
        _ = Task.Run(async () =>
        {
            var envelope = new Envelope { RequestId = 0, Message = message };
            foreach (var session in Sessions.Values)
            {
                try
                {
                    await session.SendAsync(envelope, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WS] announcement to {session.characterId} failed: {ex.Message}");
                }
            }
        });
    }

    /// <summary>Mirrors the client's chat envelope shape. <c>typeCode</c> maps to
    /// <c>ChatPresentationType</c> (4 = first to reach a world level, 6 = hardcore death).
    /// Omitted keys are tolerated by the client's formatter.</summary>
    private static string BuildAnnouncementContent(State.GameStore.WorldAnnouncement a)
    {
        var mode = ModeName(a.GameMode);
        var text = a.Kind == "hardcore"
            ? $"{a.CharacterName} has died in {mode} Hardcore!"
            : $"{a.CharacterName} is the first to reach {mode} world tier {a.WorldTier}-{a.WorldWaypoint}!";
        var payload = new Dictionary<string, string>
        {
            ["typeCode"] = a.Kind == "hardcore" ? "6" : "4",
            ["message"] = text,
            ["itemlink"] = "",
            ["avatarId"] = "0",
            ["frameId"] = "0",
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string ModeName(int gameMode) => gameMode switch
    {
        2 or 5 => "Season",
        3 or 6 => "Challenge",
        _ => "Normal",
    };

    private sealed class RealtimeSession
    {
        private readonly WebSocket socket;
        private readonly SharedNet.Dto.UserDto user;
        internal readonly Guid? characterId;
        private readonly bool appearOnline;
        private readonly Guid sessionId = Guid.NewGuid();
        private readonly object progressGate = new();
        private readonly object channelGate = new();
        private readonly HashSet<string> channels = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim sendGate = new(1, 1);
        private State.GameStore.RealtimeProgress progress;
        private bool compressed;

        public RealtimeSession(WebSocket socket, SharedNet.Dto.UserDto user, Guid? characterId, bool appearOnline)
        {
            this.socket = socket;
            this.user = user;
            this.characterId = characterId;
            this.appearOnline = appearOnline;
            if (characterId is { } cid)
                progress = State.GameStore.Instance.GetRealtimeProgress(user.UserId, cid);
            else progress = State.GameStore.RealtimeProgress.Empty;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine($"[WS] open user={user.UserId} character={characterId} online={appearOnline}");
            if (characterId is { } openedCharacter)
            {
                var header = State.GameStore.Instance.Characters(user.UserId)
                    .FirstOrDefault(h => h.CharacterId == openedCharacter);
                if (header is not null && header.GameMode is SharedNet.Constants.Game.GameMode.Season or SharedNet.Constants.Game.GameMode.SeasonHardcore)
                {
                    // The client also applies SerializedData.Buffs on load, but pushing the
                    // SeasonBuff explicitly guarantees the trophy icon appears and survives the
                    // buff-bar's start/refresh race.
                    await SendAsync(new Envelope { RequestId = 0, Message = new BuffReceivedMessage { Buff = State.GameStore.CreateSeasonBuff() } }, cancellationToken);
                    Console.WriteLine($"[WS] pushed SeasonBuff to {openedCharacter}");
                }
            }
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            try
            {
                while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    }
                    if (result.MessageType != WebSocketMessageType.Binary) continue;

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var payload = message.ToArray();
                    message.SetLength(0);
                    if (payload.Length == 0) continue;
                    compressed = LooksCompressed(payload);
                    await DispatchAsync(payload, cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException ex)
            {
                Console.WriteLine($"[WS] connection error for {characterId}: {ex.Message}");
            }
            finally
            {
                Console.WriteLine($"[WS] closed user={user.UserId} character={characterId}");
            }
        }

        private async Task DispatchAsync(byte[] payload, CancellationToken cancellationToken)
        {
            Envelope request;
            try
            {
                request = Decode(payload);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS] undecodable frame ({payload.Length}B): {ex.Message}");
                await SendAsync(new Envelope { RequestId = 0, Message = Error(ErrorCodes.UNRECOGNIZED_PAYLOAD, "Undecodable payload") }, cancellationToken);
                return;
            }

            if (request?.Message == null)
            {
                await SendAsync(new Envelope { RequestId = request?.RequestId ?? 0, Message = Error(ErrorCodes.MISSING_PAYLOAD, "Empty envelope") }, cancellationToken);
                return;
            }

            var response = await HandleAsync(request.Message, cancellationToken);
            if (response == null || request.RequestId == 0) return; // fire-and-forget / server push
            await SendAsync(new Envelope { RequestId = request.RequestId, Message = response }, cancellationToken);
        }

        private async Task<Message> HandleAsync(Message message, CancellationToken cancellationToken)
        {
            switch (message)
            {
                case UpdateCharacterMetadataMessage metadata:
                    return HandleMetadata(metadata);

                case ChannelJoinMessage join:
                    return HandleChannelJoin(join);

                case ChannelLeaveMessage leave:
                    LeaveChannel(leave.ChannelLeave?.ChannelId);
                    return new ChannelJoinResponse();

                case ChannelMessageSendMessage send:
                    return await HandleChatSendAsync(send, cancellationToken);

                case ClientQuestEventMessage:
                    return new ClientQuestEventMessageResponse();

                case ClientConversationEventMessage:
                    return new ClientConversationEventMessageResponse();

                case PartyJoinMessage join:
                    return new PartyMessage { Party = BuildParty(join.PartyId) };

                case PartyLeaveMessage leave:
                    return new PartyMessage { Party = BuildParty(leave.PartyId) };

                case PartyCharacterStateMessage partyState:
                    // Position updates are fire-and-forget; echo so the client's awaited
                    // envelope completes without changing anything.
                    return partyState;

                case ClientGuildSiegeProgressMessage:
                    return new ClientGuildSiegeProgressMessage();

                case ChannelPresenceEventMessage presence:
                    return presence;

                case CharacterVisualUpdateMessage visual:
                    return visual;

                default:
                    Console.WriteLine($"[WS] unhandled message type {message.GetType().Name} from {characterId}");
                    return new NotificationListMessage { Notifications = new List<NotificationPayload>() };
            }
        }

        private Message HandleMetadata(UpdateCharacterMetadataMessage request)
        {
            var payload = request.Metadata ?? new CharacterMetadataPayload();
            var gain = payload.BaseExperienceGained;
            if (double.IsNaN(gain) || double.IsInfinity(gain) || gain < 0) gain = 0;
            // Cap a single client-reported batch to a generous ceiling; a legit idle sync is
            // far below this, so it only stops absurd/negative values from corrupting state.
            if (gain > 5_000_000) gain = 0;

            State.GameStore.RealtimeProgress updated;
            lock (progressGate)
            {
                var experience = progress.Experience + gain;
                // Seed the wallet from the client's first sync, then treat the server as
                // authoritative (accept increases, never regress). Silver/opals earned via
                // the currency RPCs are already persisted, so a periodic metadata sync must
                // not roll them back to a stale LastSeen value.
                var first = progress.UpdatedUtc == default;
                var silver = first ? payload.LastSeenSilver : Math.Max(progress.Silver, payload.LastSeenSilver);
                var opals = first ? payload.LastSeenOpals : Math.Max(progress.Opals, payload.LastSeenOpals);
                if (characterId is { } cid)
                    updated = State.GameStore.Instance.SaveRealtimeProgress(user.UserId, cid, experience, silver, opals);
                else
                    updated = new State.GameStore.RealtimeProgress(experience, silver, opals, DateTime.UtcNow);
                progress = updated;
            }

            Console.WriteLine($"[META] character={characterId} baseGain={payload.BaseExperienceGained:F2} applied={gain:F2} total={updated.Experience:F2} result={updated.Experience:F2}");

            return new UpdateCharacterMetadataResponse
            {
                FinalExperienceGained = gain,
                NewExperience = updated.Experience,
                NewSilver = updated.Silver,
                NewOpals = updated.Opals,
                ServerTime = DateTime.UtcNow,
            };
        }

        private ChannelJoinResponse HandleChannelJoin(ChannelJoinMessage join)
        {
            var payload = join.ChannelJoin ?? new ChannelJoinPayload();
            var target = string.IsNullOrEmpty(payload.Target) ? "Global" : payload.Target;
            var id = $"{payload.Type}:{target}";
            JoinChannel(id);
            var normalized = NormalizeChannel(id);
            var self = SelfPresence(target);
            var presences = Sessions.Values
                .Where(s => s.InChannel(normalized))
                .Select(s => s.SelfPresence(target))
                .Where(p => p.SessionId != self.SessionId)
                .ToList();
            presences.Add(self);
            var channel = new ChannelDto
            {
                Id = id,
                Presences = presences,
                Self = self,
                RoomName = target,
                GroupId = null,
            };
            return new ChannelJoinResponse { Channel = channel };
        }

        private async Task<Message> HandleChatSendAsync(ChannelMessageSendMessage send, CancellationToken cancellationToken)
        {
            var channelId = string.IsNullOrEmpty(send.ChannelId) ? "Global" : send.ChannelId;
            // Self-heal: if the client never sent an explicit join, treat the send itself as
            // one so future broadcasts reach this session.
            JoinChannel(channelId);
            var now = DateTime.UtcNow;
            var room = RoomOf(channelId);
            var message = new ChannelMessageDto
            {
                ChannelId = channelId,
                MessageId = Guid.NewGuid(),
                Type = ChannelMessageType.Chat,
                SenderUserId = user.UserId,
                SenderCharacterId = characterId,
                SenderType = MessageSenderType.User,
                SenderUserDisplayName = user.DisplayName,
                SenderCharacterDisplayName = CharacterName() ?? user.DisplayName,
                SenderRole = user.Role,
                Content = send.Content,
                CreateTime = now,
                UpdateTime = now,
                Persistent = false,
                RoomName = room,
            };

            // Relay the client's JSON verbatim (it carries avatarId/frameId/gameMode/typeCode)
            // to every session in the same room, including the sender so it renders once.
            await BroadcastAsync(NormalizeChannel(channelId), new ChannelMessageMessage { ChannelMessage = message }, cancellationToken);

            var ack = new ChannelMessageAckDto
            {
                ChannelId = channelId,
                MessageId = message.MessageId,
                Type = ChannelMessageType.Chat,
                UserDisplayName = user.DisplayName,
                SenderType = MessageSenderType.User,
                SenderRole = user.Role,
                CreateTime = now,
                UpdateTime = now,
                Persistent = false,
                RoomName = room,
            };
            return new ChannelMessageAckMessage { ChannelMessageAck = ack };
        }

        private void JoinChannel(string channelId)
        {
            var key = NormalizeChannel(channelId);
            lock (channelGate) channels.Add(key);
        }

        private void LeaveChannel(string channelId)
        {
            var key = NormalizeChannel(channelId);
            lock (channelGate) channels.Remove(key);
        }

        internal bool InChannel(string normalized)
        {
            lock (channelGate) return channels.Contains(normalized);
        }

        private string CharacterName()
        {
            if (characterId is not { } cid) return null;
            return State.GameStore.Instance.Characters(user.UserId)
                .FirstOrDefault(h => h.CharacterId == cid)?.DisplayName;
        }

        private UserPresenceDto SelfPresence(string status) => new()
        {
            UserId = user.UserId,
            CharacterId = characterId,
            SessionId = sessionId,
            UserDisplayName = user.DisplayName,
            CharacterDisplayName = user.DisplayName,
            Persistence = false,
            Status = status,
            UserRole = user.Role,
        };

        private PartyDto BuildParty(Guid partyId) => new()
        {
            PartyId = partyId,
            Open = true,
            MaxSize = 4,
            Self = SelfPresence("party"),
            Leader = SelfPresence("party"),
            Presences = new List<UserPresenceDto> { SelfPresence("party") },
        };

        private static ErrorMessage Error(ErrorCodes code, string message) => new()
        {
            Code = code,
            Message = message,
            Context = new Dictionary<string, string>(),
        };

        internal async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open) return;
            var bytes = Encode(envelope, compressed);
            // A socket may be written from its own request loop and from a broadcast on
            // another connection's thread; the semaphore serializes those writes.
            await sendGate.WaitAsync(cancellationToken);
            try
            {
                if (socket.State != WebSocketState.Open) return;
                await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, cancellationToken);
            }
            finally
            {
                sendGate.Release();
            }
        }
    }
}
