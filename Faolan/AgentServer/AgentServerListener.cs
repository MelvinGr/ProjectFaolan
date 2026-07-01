using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.AgentServer
{
	public partial class AgentServerListener : Server<AgentServerPacket, AgentServerPacketFactory, AgentServerMessageFramer>, IAgentServerProvider
	{
		private readonly ClaudeChannel _claudeChannel;
		private readonly IAgentServerDispatcher _dispatcher;
		private readonly IGameServerDispatcher _gameServerDispatcher;

		public AgentServerListener(ILogger<AgentServerListener> logger, IConfiguration configuration, IDatabaseRepository database, IAgentServerDispatcher dispatcher, IGameServerDispatcher gameServerDispatcher,
			ClaudeChannel claudeChannel) : base(configuration.AgentServerPort, logger, configuration, database, new AgentServerOutgoingPacketLogger(logger))
		{
			_dispatcher = dispatcher;
			_gameServerDispatcher = gameServerDispatcher;
			_claudeChannel = claudeChannel;
		}

		public IReadOnlyList<SessionInfo> GetSessions()
		{
			return NetworkClients
				.Select(nc =>
				{
					var account = nc.Session?.Account;
					var character = nc.Session?.Character;
					return new SessionInfo(nc.IpAddress, account?.Id, account?.UserName, account?.ClientInstance ?? 0, character?.Id, character?.Name, character?.Level);
				})
				.ToArray();
		}

		public override Task StartAsync(CancellationToken cancellationToken)
		{
			_dispatcher.Register(this);
			return base.StartAsync(cancellationToken);
		}

		public override Task StopAsync(CancellationToken cancellationToken)
		{
			_dispatcher.Unregister(this);
			return base.StopAsync(cancellationToken);
		}

		protected override async Task ReceivedPacket(INetworkClient client, AgentServerPacket packet)
		{
			LogPacketReceived(packet, packet.Opcode);

			switch (packet.Opcode)
			{
				case AgentServerOpcodes.Hello:
				{
					var unk0 = packet.Body.ReadUInt32();
					var clientInstance = packet.Body.ReadUInt32();
					var accountId = packet.Body.ReadUInt32();

					client.Session.Account = await Database.GetAccount(accountId);
					if (client.Session.Account == null)
						throw new Exception($"Account not found: {accountId:X}");

					if (client.Session.Account.ClientInstance != clientInstance)
					{
						Logger.LogWarning("AgentServer hello: account {AccountId:X8} currently tracks client instance {TrackedClientInstance:X8}, but received {ClientInstance:X8}.",
							accountId,
							client.Session.Account.ClientInstance,
							clientInstance);
					}

					var resolvedCharacter = await Database.GetCharacterForAccount(accountId, clientInstance);
					client.Session.Account.ClientInstance = clientInstance;
					client.Session.Account.Character = resolvedCharacter?.Id == clientInstance
						? resolvedCharacter
						: null;
					client.Session.Character = resolvedCharacter;

					Logger.LogInformation("AgentServer hello decoded: unk0={Unk0:X}, clientInstance={ClientInstance:X8}, accountId={AccountId:X8}", unk0, clientInstance, accountId);

					// Retail sends the full chat-channel subscription list FIRST (one 0x003C per channel), then the
					// 0x0005 ready marker, the 0x0014 character info, and finally the welcome message. Verified from
					// the raw pcap. Channel ids/types are fixed per channel,
					// except ~Playfield whose id is the character's current map. ~Trial carries 0x00008244, the rest
					// 0x00008044 (a per-channel flag).
					await SendChannelSubscription(client, 0x04, 0x0000232A, "~Trial", 0x00008244);
					await SendChannelSubscription(client, 0x04, 0x00002328, "~Global", 0x00008044);
					await SendChannelSubscription(client, 0x04, 0x00002329, "~LFG", 0x00008044);
					await SendChannelSubscription(client, 0x11, 0x00002335, "~GlobalEnglish", 0x00008044);

					if (client.Session.Character?.MapId > 0)
						await SendChannelSubscription(client, 0x90, client.Session.Character.MapId, "~Playfield", 0x00008044);

					await SendChannelSubscription(client, 0x87, 0x001E8480, "~NewbieHelp", 0x00008044);

					// The server-defined Claude channel: posts on it are bridged to Claude via the MCP chat tools.
					// Only offered while the MCP server is hosted - without it nothing reads the log or can reply.
					if (Configuration.McpEnabled)
						await SendChannelSubscription(client, ClaudeChannel.ChannelType, ClaudeChannel.ChannelId, ClaudeChannel.ChannelName, ClaudeChannel.ChannelFlag);

					await new FaolanStream().WriteUInt32(0x00050000).SendAsync(client);

					await new FaolanStream()
						.WriteUInt16(AgentServerRespondsOpcodes.Ox0014)
						.WriteArrayUInt16Length(s => s
							.WriteUInt32(client.Session.Account.ClientInstance)
							.WriteUInt32(0x00000000)
							.WriteString(client.Session.Character?.Name?.Length > 0
								? client.Session.Character.Name
								: $"Character{client.Session.Account.ClientInstance}"))
						.SendAsync(client);

					await SendSystemMessage(client, Configuration.WelcomeString); //.Replace("$BUILDSTRING", Statics.BuildString));
					await SendSystemMessage(client, Statics.BuildString.Replace("\n", "<br />"));

					break;
				}

				case AgentServerOpcodes.ChannelChat:
				{
					// The client posted a message to a chat channel: [byte type][u32 channelId][string][0000].
					// Relay it to every connected client as the sender's identity (0x0014) + the message (0x0041
					// with the sender id inserted). The channel (type+id) is preserved, so each client's own channel
					// subscriptions decide whether/where it shows - that is what makes the multiple channels work
					// (global channels reach everyone; ~Playfield's id is a map, so only same-map clients are
					// subscribed to it and the rest ignore it).
					var channelType = packet.Body.ReadByte();
					var channelId = packet.Body.ReadUInt32();
					var message = packet.Body.ReadString();

					Logger.LogInformation("AgentServer channel chat from {Character} on channel {Type:X2}/{Id:X8}: {Message}", client.Session.Character?.Name ?? "?", channelType, channelId, message);

					// A '.command' (not '..') from a GM runs as a GM command in ANY channel, just like in /say -
					// routed to the player's world session via the GameServer dispatcher. The command line is NOT
					// echoed to chat (and is kept out of the Claude bridge log below so Claude doesn't "reply" to it).
					if (await TryRunChatGmCommand(client, message))
						break;

					await BroadcastChannelChat(client, channelType, channelId, message);

					// #95: persist the channel post to chat history (write-behind, never breaks the live relay above).
					await PersistChat(client, channelType, channelId, null, false, message);

					// Posts on the Claude channel are additionally recorded for the MCP bridge (Claude polls the log
					// and replies via SendClaudeChannelMessage); the normal broadcast above still shows them in-game.
					if (Configuration.McpEnabled && channelId == ClaudeChannel.ChannelId)
					{
						var senderId = client.Session.Account?.ClientInstance ?? 0;
						var senderName = client.Session.Character?.Name?.Length > 0
							? client.Session.Character.Name
							: $"Character{senderId}";
						_claudeChannel.Append(senderId, senderName, message, false);
					}

					break;
				}

				case AgentServerOpcodes.Whisper:
				{
					// A tell: [u32 targetCharacterId][string text][00 01 00] - op 0x001E both directions (whisper
					// capture 2026-06-12, Hoxxling <-> Settuc). Deliver to the target as the sender's identity
					// (0x0014, like channel chat) + the same 0x001E shape with the SENDER's id in the peer slot.
					// The sender's client renders its own outgoing tell locally - retail echoes nothing back.
					var targetCharacterId = packet.Body.ReadUInt32();
					var message = packet.Body.ReadString();
					var senderId = client.Session.Account?.ClientInstance ?? 0;
					var senderName = client.Session.Character?.Name?.Length > 0
						? client.Session.Character.Name
						: $"Character{senderId}";

					Logger.LogInformation("AgentServer whisper {Sender} -> {Target:X8}: {Message}", senderName, targetCharacterId, message);

					// #95: persist the whisper to chat history (channelType/id 0 for a tell; recipient in TargetId).
					await PersistChat(client, 0, 0, targetCharacterId, true, message);

					// A tell to Claude's synthetic identity goes into the MCP bridge log as a private message
					// (the client learns Claude's id from the channel posts' 0x0014, so /tell Claude works). The
					// whisper MODE is carried structurally (viaWhisper) so the bridge can have Claude reply privately,
					// matching how it was addressed - NOT as a "(whisper) " text prefix the bridge would have to parse.
					if (Configuration.McpEnabled && targetCharacterId == ClaudeChannel.SenderId)
					{
						_claudeChannel.Append(senderId, senderName, message, false, true);
						break;
					}

					var target = ClientForCharacter(targetCharacterId);
					if (target == null)
					{
						// Offline/unknown target: not captured (both sides were online); a system message is the
						// graceful fallback so the tell doesn't vanish silently.
						await SendSystemMessage(client, "Player is not online.");
						break;
					}

					await new FaolanStream().WriteUInt16(AgentServerRespondsOpcodes.Ox0014).WriteArrayUInt16Length(s => s.WriteUInt32(senderId).WriteUInt32(0x00000000).WriteString(senderName)).SendAsync(target);
					await target.SendAsync(BuildWhisper(senderId, message));

					break;
				}

				case AgentServerOpcodes.ChatReady:
				{
					// Small chat-init handshake (body 00 01 02); retail echoes it back verbatim.
					await new FaolanStream().WriteUInt16(AgentServerRespondsOpcodes.Ox0064).WriteArrayUInt16Length(s => s.WriteArray(packet.Body.ReadRemainingArray())).SendAsync(client);

					break;
				}

				default:
				{
					LogUnknownPacket(packet, packet.Opcode);
					break;
				}
			}
		}

		// #95: write-behind chat-history persistence. Records a channel post (isWhisper=false, target=null) or a tell
		// (isWhisper=true, target=recipient) to the ChatMessages table. Guarded: a DB hiccup must NEVER break the live
		// chat relay, so any failure is logged and swallowed. The sender identity matches the wire (ClientInstance ==
		// character id for a player; see player_handle_equals_character_id).
		private async Task PersistChat(INetworkClient client, byte channelType, uint channelId, uint? target, bool isWhisper, string message)
		{
			try
			{
				var senderId = client.Session?.Account?.ClientInstance ?? 0;
				var senderName = client.Session?.Character?.Name?.Length > 0
					? client.Session.Character.Name
					: $"Character{senderId}";
				await Database.SaveChatMessage(new ChatMessage
				{
					SentAt = DateTimeOffset.UtcNow,
					SenderId = senderId,
					SenderName = senderName,
					ChannelType = channelType,
					ChannelId = channelId,
					TargetId = target,
					IsWhisper = isWhisper,
					Text = message?.Length > 512
						? message[..512]
						: message
				});
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to persist chat message (history only; live relay unaffected).");
			}
		}

		/// <summary>
		///     If <paramref name="message" /> is a '.command' (single leading dot) from a GameMaster, run it as a GM
		///     command on the player's world session (via the GameServer dispatcher) and return true so the caller
		///     suppresses the chat broadcast/echo. Mirrors the '/say' GM gate in
		///     <see cref="GameServerListener" /> so commands work in every chat channel.
		/// </summary>
		private async Task<bool> TryRunChatGmCommand(INetworkClient client, string message)
		{
			var text = message?.TrimStart() ?? string.Empty;
			if (text.Length <= 2 || text[0] != '.' || text[1] == '.')
				return false;

			if (client.Session.Character == null || client.Session.Account is not { } account || account.Type < AccountType.GameMaster)
				return false;

			await _gameServerDispatcher.RunGmCommand(client.Session.Character.Id, text);
			return true;
		}
	}
}
