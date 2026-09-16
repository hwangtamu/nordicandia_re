using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
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

    private sealed class RealtimeSession
    {
        private readonly WebSocket socket;
        private readonly SharedNet.Dto.UserDto user;
        private readonly Guid? characterId;
        private readonly bool appearOnline;
        private readonly Guid sessionId = Guid.NewGuid();
        private readonly object progressGate = new();
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

                case ChannelLeaveMessage:
                    return new ChannelJoinResponse();

                case ChannelMessageSendMessage send:
                    return HandleChatSend(send);

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
            var self = SelfPresence(target);
            var channel = new ChannelDto
            {
                Id = $"{payload.Type}:{target}",
                Presences = new List<UserPresenceDto> { self },
                Self = self,
                RoomName = target,
                GroupId = null,
            };
            return new ChannelJoinResponse { Channel = channel };
        }

        private ChannelMessageAckMessage HandleChatSend(ChannelMessageSendMessage send)
        {
            var channelId = send.ChannelId ?? "Global";
            var ack = new ChannelMessageAckDto
            {
                ChannelId = channelId,
                MessageId = Guid.NewGuid(),
                Type = ChannelMessageType.Chat,
                UserDisplayName = user.DisplayName,
                SenderType = MessageSenderType.User,
                SenderRole = user.Role,
                CreateTime = DateTime.UtcNow,
                UpdateTime = DateTime.UtcNow,
                Persistent = false,
                RoomName = channelId.Contains(':') ? channelId[(channelId.IndexOf(':') + 1)..] : channelId,
            };
            return new ChannelMessageAckMessage { ChannelMessageAck = ack };
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

        private async Task SendAsync(Envelope envelope, CancellationToken cancellationToken)
        {
            if (socket.State != WebSocketState.Open) return;
            var bytes = Encode(envelope, compressed);
            await socket.SendAsync(bytes, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }
}
