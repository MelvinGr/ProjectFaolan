using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	/// <summary>
	///     Guild RPC handlers, wire builders and flow state for the CsPlayerAgent connection (create / invite / accept
	///     / decline / leave / MOTD / info). It owns the pending-invite state and routes guild events to other
	///     connected members. Formats are decoded from the hoxxling guild_invite_accepted capture (see
	///     tools/capture/decode_guild.py).
	/// </summary>
	/// <remarks>
	///     The cross-connection client lookup (ClientForCharacter, on the Server base) is injected as a delegate seam;
	///     the in-world guild-tag push rides <see cref="Core.Network.Server.GameServer.IGameServerDispatcher" />.
	/// </remarks>
	public sealed class GuildService
	{
		// Retail addresses every CsPlayerAgent server->client packet from the guild-server object endpoint
		// {class = the class the client targeted, instance = a fixed server-object handle}. The client targets the
		// class with no instance (its Authenticate and requests carry instance 0), and the captures show a nonzero
		// server instance on every reply (0x04 on the Hoxxling login, 0x52 on the DemoL80 create - an arbitrary server
		// handle, not session data). A constant is stamped so the s2c headers match retail's shape instead of echoing
		// the client's instance-0 endpoint back as the sender.
		private const uint GuildServerInstance = 0x04;
		private static readonly ProtoEndpoint PushSender = Endpoints.Endpoint(RpcInterfaces.CSPlayerAgentType, GuildServerInstance);
		private static readonly ProtoEndpoint PushReceiver = Endpoints.Endpoint(RpcInterfaces.CSPlayerInterfaceType, 0);

		// The s2c endpoints for an unsolicited CsPlayerAgent push (a guild event to a player who did not just send a
		// request, so there is no incoming packet to echo). Retail addresses every reply the same way, and every
		// request carries the same endpoints - target = the guild-server class (CSPlayerAgent), caller =
		// CSPlayerInterface at instance 0 - so a push reuses the exact endpoints a reply would have echoed
		// (ReplySender(packet.Receiver) / packet.Sender), and no per-client endpoint state is needed.

		private readonly ConcurrentDictionary<uint, PendingGuildInvite> _pendingGuildInvites = [];
		private readonly ConnectionRegistry _connections;
		private readonly IChatServerDispatcher _chatServerDispatcher;
		private readonly ICharacterRepository _characters;
		private readonly IGuildRepository _guilds;
		private readonly IGameServerDispatcher _gameServerDispatcher;
		private readonly ILogger _logger;

		public GuildService(ICharacterRepository characters, IGuildRepository guilds, ILogger<CsPlayerAgentListener> logger, IGameServerDispatcher gameServerDispatcher, IChatServerDispatcher chatServerDispatcher,
			[FromKeyedServices(CsPlayerAgentServices.ConnectionsKey)] ConnectionRegistry connections)
		{
			_characters = characters;
			_guilds = guilds;
			_logger = logger;
			_gameServerDispatcher = gameServerDispatcher;
			_chatServerDispatcher = chatServerDispatcher;
			_connections = connections;
		}

		// 0x201D DeclineGuildInvitation: drop the pending invite and tell the inviter (0x2031 with joined=0 - the same
		// message the accept sends with 1). Returns the declined invite's guild name, or null when there was none, so
		// the listener can log it.
		public async Task<string?> DeclineGuildInvitation(uint characterId, string? declinerName)
		{
			if (!_pendingGuildInvites.TryRemove(characterId, out var declined))
				return null;

			if (_connections.ForCharacter(declined.InviterId) is { } inviterClient)
				await SendInviteReplyToInviter(inviterClient, declinerName ?? "", false);

			return declined.GuildName;
		}

		// 0x201C AcceptGuildInvitation(i_cGuildID): body is the guild ID32 from the 0x202A invite. Retail replies
		// with the 0x203F full update followed by the joiner's own 0x202F member-joined record (which also fans
		// out to the other online members), and sets the in-world tag with a GameServer 0x2550C99A.
		public async Task HandleAcceptGuildInvitation(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			body.ReadUInt32(); // ID32 type (0x0000DEB7)
			var guildId = body.ReadUInt32();

			if (!_pendingGuildInvites.TryRemove(character.Id, out var invite) || invite.GuildId != guildId)
			{
				_logger.LogWarning("AcceptGuildInvitation from {CharacterId:X8} for guild {GuildId:X} without a matching pending invite.", character.Id, guildId);
				return;
			}

			if (!await _characters.SetCharacterGuild(character.Id, guildId, 0))
				return;

			character.GuildId = guildId;
			character.GuildRank = 0;

			var guild = await _guilds.GetGuild(guildId);
			if (guild == null)
				return;

			_logger.LogInformation("{Character} joined guild \"{Guild}\" (invited by {Inviter}).", character.Name, guild.Name, invite.InviterName);
			await SendGuildFullUpdate(client, ReplySender(packet.Receiver), packet.Sender, guild, character);
			await BroadcastGuildMemberJoined(guild, character); // includes the joiner, matching retail order
			if (_connections.ForCharacter(invite.InviterId) is { } inviterClient)
				await SendInviteReplyToInviter(inviterClient, character.Name ?? "", true);

			await _gameServerDispatcher.SendGuildTag(character.Id, guild.Name);
			await _chatServerDispatcher.RefreshGuildChannel(character.Id, guild.Id); // the guild chat tab
		}

		// 0x201E ClientRequestGuildInfo([ID32 char]): the client resolves guild banners/tooltips of other
		// players with this. Reply is 0x2042 when the target is in the requester's own guild, 0x2043 (carrying
		// the guild name) when it is in another guild, and nothing when the target is guildless.
		public async Task HandleClientRequestGuildInfo(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // ID32 type (0x0000C350)
			var targetId = body.ReadUInt32();

			var target = await _characters.GetCharacter(targetId);
			if (target?.GuildId is not { } targetGuildId)
				return;

			if (client.Session.Character?.GuildId == targetGuildId)
			{
				// [charId][guild ID32][03][02][charId][01] - the constants are not decoded (they look like
				// rank/flags); taken verbatim from the capture's same-guild reply.
				await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchGuildUpdateMember,
					b => b.WriteUInt32(targetId).WriteUInt32(EntityClass.Guild).WriteUInt32(targetGuildId).WriteByte(0x03).WriteByte(0x02).WriteUInt32(targetId).WriteByte(0x01)));
				return;
			}

			var guild = await _guilds.GetGuild(targetGuildId);
			if (guild == null)
				return;

			// [charId][guild ID32][03][guildId][02][05][name][empty list frame][u32 0][u32 0] - verbatim from the
			// capture's "The Undreamed" reply; the list frame is a count word for an empty list, not guild data.
			await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchGuildUpdateOther,
				b => b
					.WriteUInt32(targetId)
					.WriteUInt32(EntityClass.Guild)
					.WriteUInt32(targetGuildId)
					.WriteByte(0x03)
					.WriteUInt32(targetGuildId)
					.WriteByte(0x02)
					.WriteByte(0x05)
					.WriteString(guild.Name)
					.WriteUInt32(ListFrame.Empty)
					.WriteUInt32(0)
					.WriteUInt32(0)));
		}

		// 0x201F ClientRequestGuildName([ID32 guild]): the client resolving a guild id it has seen (a roster entry, a
		// renown ranking row) to a display name. Body is [u32 EntityClass.Guild][u32 guildId], reply is
		// DispatchGuildName [u32 guildId][string name] - not an ID32 on the way back, matching the field's `n` prefix
		// (`i_nGuildInstance`). Both directions are capture-verified as a pair: the request at frame 1120 asks for
		// guild 0x00075E9A and frame 1131 answers `00075e9a 000b "the chickas"`.
		// [V2: xxx_2clients_jumping.tshark.json frame 1131]
		public async Task HandleClientRequestGuildName(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // ID32 type (0x0000DEB7)
			var guildId = body.ReadUInt32();

			var guild = await _guilds.GetGuild(guildId);
			if (guild == null)
			{
				// Retail sends nothing for a guild it cannot resolve, and the client keeps showing the raw id. Say so
				// rather than inventing a placeholder name that would look like real data in the UI.
				_logger.LogWarning("ClientRequestGuildName for unknown guild 0x{GuildId:X8}; no reply.", guildId);
				return;
			}

			await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchGuildName,
				GuildNameCodec.Build(new GuildName(guildId, guild.Name))));
		}

		// 0x2021 CreateGuild. Body decoded from DemoL80_..._create-guild.pcapng
		// (docs/subsystems/create-guild-decode.md): [name s16][u8 form][u16 0], e.g. `0006 "faolan" 06 0000`. The
		// `form` is the government/guild-form the player picked in the registrar window. Retail's immediate reply is
		// DispatchGuildCreated (0x2044), a [creator member record][guild header][renown/trailer] packet that opens the
		// window with no relog; the 0x203F full update sent here is what makes the guild show after a relog.
		public async Task HandleCreateGuild(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			var name = body.ReadString();
			var form = (GuildGoverningForm)(body.Length - body.Position >= 1
				? body.ReadByte()
				: (byte)GuildGoverningForm.Cimmerian); // i_nGoverningForm (docs/subsystems/create-guild-decode.md)
			if (string.IsNullOrWhiteSpace(name) || character.GuildId != null)
			{
				_logger.LogWarning("CreateGuild rejected for {CharacterId:X8}: name \"{Name}\", already in guild: {InGuild}.", character.Id, name, character.GuildId != null);
				return;
			}

			var guild = await _guilds.CreateGuild(name, character.Id);
			if (guild == null)
			{
				// Retail's name-taken refusal is a no-arg dispatch: the client already knows which name it submitted,
				// so the whole message is the method id. Captured in `annotated_6`, where a `CreateGuild "Faolan"` is
				// answered in the same second by `0x2047` with an empty body and nothing else. Without it the registrar
				// window sits there.
				_logger.LogWarning("CreateGuild failed for {CharacterId:X8}: guild name \"{Name}\" is taken.", character.Id, name);
				await client.SendAsync(new Rpc(ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchGuildTakenName, _ => { }));
				return;
			}

			character.GuildId = guild.Id;

			// The governing form has to be stored, because the rank ladder derives from it: the leader's rung is
			// per-form - Stygian tops out at 3, Cimmerian and Harem at 4, the other four at 5 - so a flat
			// GuildRank.Leader would be right for only two forms of seven.
			guild.GovernmentForm = form;
			character.GuildRank = (GuildRank)GuildRankLadder.LeaderRank(form);

			_logger.LogInformation("Guild \"{Name}\" ({GuildId:X}) created by {Character} as {Form}; leader rank {Rank} (\"{RankName}\").", guild.Name, guild.Id, character.Name, form,
				(byte)character.GuildRank, GuildRankLadder.NameOf(form, (byte)character.GuildRank!) ?? "?");
			// Retail's immediate create response is 0x2044 DispatchGuildCreated, which opens the window with no relog.
			// If the client does not accept it the guild still shows on the next login through the re-auth 0x203F push.
			await SendGuildCreated(client, ReplySender(packet.Receiver), packet.Sender, guild, character, (byte)form);
			await _gameServerDispatcher.SendGuildTag(character.Id, guild.Name);

			// The guild chat channel, which is what makes the guild chat tab appear. Retail sends it right after the
			// 0x2044 (create capture: 0x2044 at frame 366, the `03 <guildId> "~Guild"` subscription at 369), so the
			// order here is the same, on the chat connection rather than this one.
			await _chatServerDispatcher.RefreshGuildChannel(character.Id, guild.Id);
		}

		// 0x2023 GuildInvite: same as by-name but the target is identified by ID32.
		public async Task HandleGuildInvite(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // ID32 type (0x0000C350)
			var targetId = body.ReadUInt32();
			var target = await _characters.GetCharacter(targetId);
			await InviteCharacterToGuild(client, target, $"{targetId:X8}");
		}

		// 0x2022 GuildInviteByName(i_cCharacterName): inviter must be guilded; the target must be online (the
		// invite dialog is a live push - retail also rejects offline targets).
		public async Task HandleGuildInviteByName(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var targetName = body.ReadString();
			var target = await _characters.GetCharacterByName(targetName, client.Session.Character.RealmId);
			await InviteCharacterToGuild(client, target, targetName);
		}

		// 0x2028 GuildLeave (no body): the member takes themselves out.
		public Task HandleGuildLeave(INetworkClient client)
		{
			return client.Session.Character is { } character
				? RemoveFromGuild(character, "left")
				: Task.CompletedTask;
		}

		// 0x2027 GuildKick([ID32 target]): an officer takes someone else out. Same removal, different authority check -
		// and the kicked member is told by the same DispatchGuildMemberLeft everyone else gets, because it names them.
		public async Task HandleGuildKick(INetworkClient client, FaolanStream body)
		{
			var actor = client.Session.Character;
			var targetId = EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id;

			var target = await AuthorizeRankAction(actor, targetId, "kick", GuildPermission.KickOthers);
			if (target == null)
				return;

			await RemoveFromGuild(target, $"was kicked by {actor!.Name}");
		}

		// 0x2025 GuildPromote / 0x2026 GuildDemote ([ID32 target]): move a member one rung up or down their guild's
		// ladder. Which rungs exist is per governing form (GuildRankLadder), so the bounds are the form's rather than a
		// constant - a Stygian guild has four rungs and an Aquilonian six.
		//
		// The top rung is the leader's and is not reachable by promotion: handing it out would create a second leader,
		// and retail transfers leadership through the governing-form change instead (DispatchGuildGoverningFormChanged
		// carries a `newLeader`). So promotion stops one below it.
		public async Task HandleGuildRankChange(INetworkClient client, FaolanStream body, bool promote)
		{
			var actor = client.Session.Character;
			var targetId = EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id;

			var target = await AuthorizeRankAction(actor, targetId, promote
				? "promote"
				: "demote", promote
				? GuildPermission.Promote
				: GuildPermission.Demote);
			if (target == null)
				return;

			var guild = await _guilds.GetGuild(target.GuildId!.Value);
			if (guild == null)
				return;

			var oldRank = (byte)(target.GuildRank ?? 0);
			var leaderRank = GuildRankLadder.LeaderRank(guild.GovernmentForm);
			var newRank = promote
				? (byte)Math.Min(oldRank + 1, Math.Max(leaderRank - 1, 0))
				: (byte)Math.Max(oldRank - 1, 0);

			if (newRank == oldRank)
			{
				_logger.LogInformation("Guild rank change refused: {Target} is already at rung {Rank} of \"{Guild}\"'s ladder.", target.Name, oldRank, guild.Name);
				return;
			}

			if (!await _characters.SetCharacterGuild(target.Id, guild.Id, (GuildRank)newRank))
				return;

			target.GuildRank = (GuildRank)newRank;
			_logger.LogInformation("Guild \"{Guild}\": {Actor} {Verb}d {Target} from rung {Old} to {New} ({Name}).", guild.Name, actor!.Name, promote
				? "promote"
				: "demote", target.Name, oldRank, newRank, GuildRankLadder.NameOf(guild.GovernmentForm, newRank) ?? "?");

			// Three pushes, each to a different audience - see docs/subsystems/guilds.md for why the client needs all
			// three: the mover's confirmation, the moved member's own notification, and everyone's roster row.
			await SendRankFeedback(client, guild.GovernmentForm, oldRank, newRank, target.Name ?? "", promote);
			if (_connections.ForCharacter(target.Id) is { } targetClient)
				await SendRankChanged(targetClient, newRank, oldRank);

			await BroadcastMemberUpdate(guild, target);
		}

		// The shared authority check for kick / promote / demote: the actor's rung must carry the right, and the
		// action may not be aimed at themselves or at someone who outranks them. Returns the target when allowed,
		// else null (and logs why).
		//
		// The right comes from the client's own per-rung permission bitmask (RDB 1000518), so these are not
		// leader-only: `Officer`-tier rungs and up may invite, kick, promote and demote, which is one to two rungs
		// below the leader depending on the governing form.
		// [V1: tools/ghidra/DumpGuildRankPermissions.java]
		//
		// "May not aim it at an equal or higher rung" is this server's rule - the table grants a right, it does not say
		// who it may be used on, and without that guard an Aquilonian Patrician could demote their own Potentate. It is
		// the narrow half of the choice: refusing an action retail might allow only inconveniences an officer.
		private async Task<Character?> AuthorizeRankAction(Character? actor, uint targetId, string verb, GuildPermission permission)
		{
			if (actor?.GuildId is not { } guildId)
			{
				_logger.LogWarning("Guild {Verb} by {Actor}: actor is not in a guild.", verb, actor?.Name);
				return null;
			}

			var guild = await _guilds.GetGuild(guildId);
			if (guild == null)
				return null;

			var actorRank = (byte)(actor.GuildRank ?? 0);
			if (!GuildRankLadder.Allows(guild.GovernmentForm, actorRank, permission))
			{
				_logger.LogInformation("Guild {Verb} refused: {Actor} is rank {Rank} (\"{RankName}\") in \"{Guild}\", which may not {Verb}.", verb, actor.Name, actorRank,
					GuildRankLadder.NameOf(guild.GovernmentForm, actorRank) ?? "?", guild.Name, verb);
				return null;
			}

			var target = await _characters.GetCharacter(targetId);
			if (target == null || target.GuildId != guildId || target.Id == actor.Id)
			{
				_logger.LogInformation("Guild {Verb} refused: target {TargetId:X8} is not another member of \"{Guild}\".", verb, targetId, guild.Name);
				return null;
			}

			if ((byte)(target.GuildRank ?? 0) >= actorRank)
			{
				_logger.LogInformation("Guild {Verb} refused: {Target} is rank {TargetRank} in \"{Guild}\", which {Actor} at rank {Rank} does not outrank.", verb, target.Name,
					(byte)(target.GuildRank ?? 0), guild.Name, actor.Name, actorRank);
				return null;
			}

			return target;
		}

		// Takes `character` out of their guild and tells everyone who can see the roster, the departing member
		// included - DispatchGuildMemberLeft names them, so it is their own notification too.
		private async Task RemoveFromGuild(Character character, string reason)
		{
			if (character.GuildId is not { } guildId)
				return;

			var guild = await _guilds.GetGuild(guildId);
			if (!await _characters.SetCharacterGuild(character.Id, null, 0))
				return;

			character.GuildId = null;
			character.GuildRank = 0;

			_logger.LogInformation("{Character} {Reason} guild {GuildId:X}.", character.Name, reason, guildId);
			await _gameServerDispatcher.SendGuildTag(character.Id, "");
			await _chatServerDispatcher.RefreshGuildChannel(character.Id, null); // drop the ~Guild channel with the tag

			if (guild == null)
				return;

			// Guild.Members was loaded before the removal, so it still contains the departing member - which is what we
			// want: retail's roster fan-out reaches them too, and their own client is the one that has to close the
			// guild window.
			foreach (var member in guild.Members)
			{
				if (_connections.ForCharacter(member.Id) is { } memberClient)
					await SendMemberLeft(memberClient, character);
			}
		}

		// Pushes one member's changed roster row to every online member (rank, online flag, playfield). The narrow
		// counterpart of re-sending the whole 0x203F full update.
		private async Task BroadcastMemberUpdate(Guild guild, Character member)
		{
			foreach (var recipient in guild.Members)
			{
				if (_connections.ForCharacter(recipient.Id) is { } recipientClient)
					await SendMemberUpdate(recipientClient, member);
			}
		}

		// 0x202E GuildMessageOfTheDay(i_cGuildMessageOfTheDay): persists the MOTD and pushes the targeted MOTD-change
		// packet (0x2057) to every online member so their guild window's MOTD refreshes + the toast pops.
		public async Task HandleGuildMessageOfTheDay(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not { } guildId)
				return;

			var motd = body.ReadString();
			var guild = await _guilds.GetGuild(guildId);
			if (guild == null)
				return;

			guild.MessageOfTheDay = motd;
			await _guilds.SaveGuild(guild);
			_logger.LogInformation("Guild \"{Guild}\" MOTD set by {Character} ({Length} chars).", guild.Name, character.Name, motd.Length);

			foreach (var member in guild.Members)
			{
				var memberClient = _connections.ForCharacter(member.Id);
				if (memberClient?.Session.Character == null)
					continue;

				await SendGuildMessageOfTheDayChanged(memberClient, PushSender, PushReceiver, motd);
			}
		}

		// The deposit half of the guild bank: the world tier has already debited the payer's wallet, so this only has
		// to credit the guild and tell everyone. The exact mirror of the withdrawal below, tier for tier - there the CS
		// side debits first and the world side credits, here the world side debits first and this credits. In both
		// cases whoever holds the money moves it first, which is the rule that matters in a money handler: a failure
		// between the two steps must lose the transfer, never duplicate it.
		//
		// Returns false without crediting when the payer is in no guild or the guild is not on this realm's CS tier,
		// which is the caller's signal to put the money back in the wallet.
		public async Task<bool> DepositToGuildBank(uint characterId, ulong amountTin)
		{
			if (amountTin == 0)
				return false;

			var character = await _characters.GetCharacter(characterId);
			if (character?.GuildId is not { } guildId)
				return false;

			var guild = await _guilds.GetGuild(guildId);
			if (guild == null)
				return false;

			guild.Cash += amountTin;
			await _guilds.SaveGuild(guild);
			_logger.LogInformation("Guild \"{Guild}\": {Character} deposited {Amount} tin (bank {Balance}).", guild.Name, character.Name, amountTin, guild.Cash);

			await BroadcastGuildCash(guild);
			return true;
		}

		// Everyone who can see the guild window sees the new balance. Shared by both directions of the bank, because
		// a deposit and a withdrawal are the same event as far as the window is concerned.
		private async Task BroadcastGuildCash(Guild guild)
		{
			foreach (var member in guild.Members)
			{
				if (_connections.ForCharacter(member.Id) is { } memberClient)
					await memberClient.SendAsync(new Rpc(PushSender, PushReceiver, GuildUpdateCashCodec.Method, GuildUpdateCashCodec.Build(guild.Cash)));
			}
		}

		// 0x2035 GuildWithdrawMoney(uint64 amount): the guild bank pays out to a member's wallet. Its deposit
		// counterpart is DepositToGuildBank above - the client's `GameCharAgent.GuildTransferMoney`, which arrives on
		// the world tier because that is where the wallet is.
		//
		// The transaction spans both tiers: the CS tier owns Guild.Cash and the rank ladder, the game tier owns the
		// wallet and is the only one whose UpdateCash the client renders. So the guild is debited here and the payout
		// goes through IGameServerDispatcher.CreditCharacterCash.
		//
		// The order is deliberately the pessimistic one: debit and persist the guild first, then credit. If the credit
		// is refused (the member is not on this realm's game host) the guild is refunded. The other way round, a
		// failure between the two steps would leave money in both places - losing a withdrawal is recoverable, minting
		// one is not.
		public async Task HandleGuildWithdrawMoney(INetworkClient client, FaolanStream body)
		{
			var character = client.Session.Character;
			var request = GuildWithdrawMoneyCodec.Parse(body.ReadRemainingArray());

			if (character?.GuildId is not { } guildId)
				return;

			var guild = await _guilds.GetGuild(guildId);
			if (guild == null)
				return;

			// Who may withdraw is the client's own rule rather than a local policy: RDB 1000518 ships a permission
			// bitmask per rung and `GuildPermission.WithdrawMoney` is one of its bits. That works out as the top two
			// rungs on six governing forms and the leader alone on Stygian.
			// [V1: tools/ghidra/DumpGuildRankPermissions.java]
			var rank = (byte)(character.GuildRank ?? 0);
			if (!GuildRankLadder.Allows(guild.GovernmentForm, rank, GuildPermission.WithdrawMoney))
			{
				_logger.LogInformation("GuildWithdrawMoney refused: {Character} is rank {Rank} (\"{RankName}\") in \"{Guild}\", which may not withdraw.", character.Name, rank,
					GuildRankLadder.NameOf(guild.GovernmentForm, rank) ?? "?", guild.Name);
				return;
			}

			if (request.AmountTin == 0 || request.AmountTin > guild.Cash)
			{
				_logger.LogInformation("GuildWithdrawMoney refused: {Character} asked for {Amount} tin of \"{Guild}\"'s {Balance}.", character.Name, request.AmountTin, guild.Name, guild.Cash);
				return;
			}

			guild.Cash -= request.AmountTin;
			await _guilds.SaveGuild(guild);

			if (!await _gameServerDispatcher.CreditCharacterCash(character.Id, request.AmountTin))
			{
				// Nobody could receive it - put it back rather than leave it nowhere.
				guild.Cash += request.AmountTin;
				await _guilds.SaveGuild(guild);
				_logger.LogWarning("GuildWithdrawMoney rolled back: {Character} is not on this realm's game host.", character.Name);
				return;
			}

			_logger.LogInformation("Guild \"{Guild}\": {Character} withdrew {Amount} tin (bank {Balance}).", guild.Name, character.Name, request.AmountTin, guild.Cash);

			await BroadcastGuildCash(guild);
		}

		// Pushes guild state to a freshly authenticated guilded character: retail sends the 0x203F full update
		// right after the Authenticate ack.
		public async Task SendGuildStateOnLogin(INetworkClient client, Rpc packet)
		{
			var character = client.Session.Character;
			if (character?.GuildId is not { } guildId)
				return;

			var guild = await _guilds.GetGuild(guildId);
			if (guild == null)
				return;

			await SendGuildFullUpdate(client, ReplySender(packet.Receiver), packet.Sender, guild, character);
		}

		// Fans a member-joined record out to every online member of the guild (their reply endpoints were
		// captured at Authenticate).
		private async Task BroadcastGuildMemberJoined(Guild guild, Character joined)
		{
			foreach (var member in guild.Members)
			{
				var memberClient = _connections.ForCharacter(member.Id);
				if (memberClient?.Session.Character == null)
					continue;

				await SendGuildMemberJoined(memberClient, PushSender, PushReceiver, joined);
			}
		}

		private async Task InviteCharacterToGuild(INetworkClient client, Character? target, string targetLabel)
		{
			var inviter = client.Session.Character;
			if (inviter?.GuildId is not { } guildId)
			{
				_logger.LogWarning("Guild invite by {Inviter}: inviter is not in a guild.", inviter?.Name);
				return;
			}

			if (target == null || target.GuildId != null)
			{
				_logger.LogWarning("Guild invite to \"{Target}\" by {Inviter}: target {Reason}.", targetLabel, inviter.Name, target == null
					? "not found"
					: "is already in a guild");
				// 0x2034 answers the no-such-name half only. A target who exists but is already guilded is a different
				// refusal, and the client's catalog has no code for it - so that one stays silent rather than borrowing
				// this message and telling the inviter a real character does not exist.
				if (target == null)
					await SendInviteNoSuchName(client, targetLabel);

				return;
			}

			var targetClient = _connections.ForCharacter(target.Id);
			if (targetClient?.Session.Character == null)
			{
				_logger.LogWarning("Guild invite to \"{Target}\" by {Inviter}: target is not online on this listener.", targetLabel, inviter.Name);
				return;
			}

			var guild = await _guilds.GetGuild(guildId);
			if (guild == null)
				return;

			_pendingGuildInvites[target.Id] = new PendingGuildInvite(guild.Id, guild.Name, inviter.Id, inviter.Name!);
			await SendGuildInvite(targetClient, PushSender, PushReceiver, guild, target.Id, inviter.Name!);
			_logger.LogInformation("Guild invite: {Inviter} invited {Target} to \"{Guild}\".", inviter.Name, target.Name, guild.Name);
		}

		// True when the character is connected to a world GameServer right now (drives the member record's
		// online flag; the member list has no other liveness source).
		private bool IsCharacterOnline(uint characterId)
		{
			return _gameServerDispatcher.GetWorldSessions().Any(s => s.Character?.Id == characterId);
		}

		// 0x203F full guild update - the packet that makes the client consider itself a guild member (guild window,
		// guild chat tab, MOTD popup). Retail pushes it right after the Authenticate ack on login and again on
		// join/member-list requests. The three arguments and their framing live in GuildFullUpdateCodec; the renown
		// block is built per-guild from Guild.Renown.
		private async Task SendGuildFullUpdate(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, Character self)
		{
			// The roster must include every member, the viewing player included - see the codec's header for why an
			// empty member map crashes the guild-management panel.
			var roster = guild.Members.Where(m => m.Name != null).Select(MemberRecord).ToList();
			var body = GuildFullUpdateCodec.Build(new GuildFullUpdate(GuildHeaders.ForFullUpdate(guild), MemberRecord(self), roster));

			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildUpdate, body));
		}

		// 0x202F member joined. Retail fans this out to every online guild member (including the joiner, right after
		// its 0x203F).
		private Task SendGuildMemberJoined(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Character member)
		{
			return client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildMemberJoined, GuildMemberJoinedCodec.Build(MemberRecord(member))));
		}

		// 0x202E member left - the departure twin of the above, carrying only the id and the name (the client's reader
		// says so; see GuildMemberLeftCodec). Sent to every online member, the departing one included.
		private static Task SendMemberLeft(INetworkClient client, Character member)
		{
			return client.SendAsync(new Rpc(PushSender, PushReceiver, CSPlayerInterfaceMethods.DispatchGuildMemberLeft, GuildMemberLeftCodec.Build(member.Id, member.Name ?? "")));
		}

		// 0x2030 member update - one roster row, for a change that is not a join or a leave (today: a rank move).
		private Task SendMemberUpdate(INetworkClient client, Character member)
		{
			return client.SendAsync(new Rpc(PushSender, PushReceiver, CSPlayerInterfaceMethods.DispatchGuildMemberUpdate, GuildMemberUpdateCodec.Build(MemberRecord(member))));
		}

		// 0x2032 / 0x2033 - the guild master's own confirmation of a promote/demote. The verb is the opcode; the body
		// is identical either way, which is why the codec declares both methods.
		private static Task SendRankFeedback(INetworkClient client, GuildGoverningForm form, byte oldRank, byte newRank, string name, bool promote)
		{
			return client.SendAsync(new Rpc(PushSender, PushReceiver, promote
				? CSPlayerInterfaceMethods.DispatchGuildGMPromoteFeedback
				: CSPlayerInterfaceMethods.DispatchGuildGMDemoteFeedback, GuildRankFeedbackCodec.Build(form, oldRank, newRank, name)));
		}

		// 0x2065 - "your rank changed", to the member it happened to. Two bytes, new before old.
		private static Task SendRankChanged(INetworkClient client, byte newRank, byte oldRank)
		{
			return client.SendAsync(new Rpc(PushSender, PushReceiver, CSPlayerInterfaceMethods.DispatchGuildRankChanged, GuildRankChangedCodec.Build(newRank, oldRank)));
		}

		// 0x2031 - what the inviter hears back once the invitee answers: the invitee's name, and whether they joined.
		private static Task SendInviteReplyToInviter(INetworkClient client, string inviteeName, bool joined)
		{
			return client.SendAsync(new Rpc(PushSender, PushReceiver, CSPlayerInterfaceMethods.DispatchGuildInviteReplyToInviter, GuildInviteReplyCodec.Build(inviteeName, joined)));
		}

		// 0x2034 - the inviter typed a name nobody answers to. Echoes the name so the client can put it in the message.
		private static Task SendInviteNoSuchName(INetworkClient client, string name)
		{
			return client.SendAsync(new Rpc(PushSender, PushReceiver, CSPlayerInterfaceMethods.DispatchGuildInviteNoSuchName, GuildInviteNoSuchNameCodec.Build(name)));
		}

		// A stored member as a roster record, with the live-session fields (online / playfield / last-seen) resolved
		// here - the only place that knows who is connected.
		private GuildMemberRecord MemberRecord(Character member)
		{
			var online = IsCharacterOnline(member.Id);
			var lastSeen = online || member.LastConnection == null
				? 0u
				: (uint)member.LastConnection.Value.ToUnixTimeSeconds();

			return GuildMemberRecords.FromCharacter(member, online, online
				? member.MapId
				: 0, lastSeen);
		}

		// The reply sender endpoint for a CsPlayerAgent s2c packet: the class the client addressed, stamped with the
		// guild-server instance (see GuildServerInstance).
		public static ProtoEndpoint ReplySender(ProtoEndpoint clientTarget)
		{
			return new ProtoEndpoint
			{
				Type = clientTarget.Type,
				Instance = GuildServerInstance
			};
		}

		// 0x2044 DispatchGuildCreated - retail's immediate create response, which opens the guild window with no relog
		// (the 0x203F GuildUpdate only takes effect on the next login). Grammar in GuildCreatedCodec. The creator is
		// online (they just made it) and their playfield is their current map token.
		private static async Task SendGuildCreated(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, Character creator, byte form)
		{
			var body = GuildCreatedCodec.Build(new GuildCreated(GuildMemberRecords.FromCharacter(creator, true, creator.MapId, 0), GuildHeaders.ForGuildCreate(guild, form)));

			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildCreated, body));
		}

		private static Task SendGuildInvite(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, Guild guild, uint inviteeId, string inviterName)
		{
			var body = GuildInviteCodec.Build(new GuildInvite(guild.Id, guild.Name, inviteeId, inviterName));

			return client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildInvite, body));
		}

		// 0x2057 DispatchGuildMessageOfTheDayChanged(i_cMessageOfTheDay): retail's targeted MOTD-change push - updates
		// the stored MOTD and pops the "message of the day" toast without re-sending the whole roster, as 0x203F would.
		// There is no capture for it; the body is the single string argument from the client method signature,
		// s16-framed like the other guild strings (docs/re/aoc-client-rpc-methods.json).
		private static async Task SendGuildMessageOfTheDayChanged(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, string motd)
		{
			await client.SendAsync(new Rpc(sender, receiver, CSPlayerInterfaceMethods.DispatchGuildMessageOfTheDayChanged, b => b.WriteString(motd)));
		}

		// A guild invite offered to a character, awaiting AcceptGuildInvitation/DeclineGuildInvitation. Keyed by the
		// invitee's character id; entries are dropped on accept or decline. Retail also expires them, which this does
		// not.
		private sealed record PendingGuildInvite(uint GuildId, string GuildName, uint InviterId, string InviterName);
	}
}
