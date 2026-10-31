using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Helpers;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.GroupFinder;
using Faolan.Core.Network.Codecs.Pvp;
using Faolan.Core.Network.Codecs.Raid;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	public partial class CsPlayerAgentListener : RpcServer, ICsPlayerAgentProvider
	{
		// Owns the guild RPC handlers, wire builders, and pending-invite / reply-endpoint state. The cross-connection
		// lookup (ClientForCharacter, on the Server base) is injected as a delegate seam.
		private readonly GuildService _guild;

		// World-listener seam: lets the guild flow check who is online (member records) and push the in-world
		// guild tag (0x2550C99A) onto a character's live GameServer connection on join/leave.
		private readonly IGameServerDispatcher _gameServerDispatcher;

		// Owns the team roster and its dispatches - see TeamService.
		private readonly TeamService _teams;

		// Raids, which are teams one level up - see RaidService. Built on the team service rather than beside it,
		// because a raid holds team ids and nothing else.
		private readonly RaidService _raids;

		// The group-finder signup flow - the quest-log gate, the role/ready handshake, and the private-team entry into
		// the activity's instance. Built on the team service the same way raids are, because the finder's product is a
		// team.
		private readonly GroupFinderService _groupFinder;

		// The PvP minigame queue - the signup, the withdrawal and the live board the minigame window polls. Built on
		// the team service too, because `i_bBringTeam` queues a whole team.
		private readonly PvpMatchMakingService _pvpMatchMaking;

		// Custom PvP matches - the create/join/side/start lifecycle on top of the queue, and the seam the world entry
		// asks for the match-owned playfield instance.
		private readonly PvpMatchService _pvpMatches;

		// Our own registration slot, so the world listener can route LeaveTeam/KickFromTeam here.
		private readonly ICsPlayerAgentDispatcher _csPlayerAgentDispatcher;
		private readonly WorkLoop _loop;

		// The port is the hosting realm's CsPlayerAgentPort (its Realm row is the single source of truth for the
		// realm-local service ports), taken by injection rather than read from global config: `HostRealm` is a
		// singleton the pre-host bootstrap registers, which is what lets this be a plain AddHostedService<T>().
		// Every collaborator arrives by injection (`CsPlayerAgentServices.AddRealmCsPlayerAgent`); nothing is `new`-ed
		// here and nothing is handed `this`. The services reach a character's CS client through the shared, keyed
		// `ConnectionRegistry`, the same instance ServerBase fills as clients connect.
		public CsPlayerAgentListener(HostRealm realm, ILogger<CsPlayerAgentListener> logger, IConfiguration configuration, IDatabaseRepository database,
			IGameServerDispatcher gameServerDispatcher, ICsPlayerAgentDispatcher csPlayerAgentDispatcher, [FromKeyedServices(CsPlayerAgentServices.ConnectionsKey)] ConnectionRegistry connections,
			[FromKeyedServices(CsPlayerAgentServices.LoopKey)] WorkLoop loop, GuildService guild, TeamService teams, RaidService raids, GroupFinderService groupFinder,
			PvpMatchMakingService pvpMatchMaking, PvpMatchService pvpMatches)
			: base(realm.CsPlayerAgentPort, logger, configuration, database, new ConanPacketTracer(logger, "CsPlayerAgent", typeof(CSPlayerInterfaceMethods)), connections)
		{
			_gameServerDispatcher = gameServerDispatcher;
			_csPlayerAgentDispatcher = csPlayerAgentDispatcher;
			_loop = loop;

			// The chat dispatcher is not held here: only the guild and team flows take it. A membership change moves
			// that member's ~Guild / ~Team chat-channel subscription, which lives on their chat connection, not this
			// one.
			_guild = guild;
			_teams = teams;

			// The raid service takes no chat dispatcher: it subscribes no ~Raid channel. That is a gap in the evidence
			// rather than a retail fact - the corpus holds no raid session at all, so only ~Guild and ~Team are ever
			// seen subscribed. Whether retail has a ~Raid channel is unknown, and a raid capture is what would settle
			// it; one is not invented in the meantime.
			_raids = raids;

			_groupFinder = groupFinder;
			_pvpMatchMaking = pvpMatchMaking;
			_pvpMatches = pvpMatches;
		}

		public override Task StartAsync(CancellationToken cancellationToken)
		{
			// Registered here rather than injected, so the world listener can reach the team roster it does not own.
			_csPlayerAgentDispatcher.Register(this);
			return base.StartAsync(cancellationToken);
		}

		// The matchmaking board is live state: a signup counted for a character who has logged off is a row the next
		// poller reads as a queued player. Retail's board only ever shows who is there, so the signup goes when the
		// connection does. The group-finder pending signup and the LFG advert ride along for the same reason.
		protected override async Task ClientDisconnected(INetworkClient client)
		{
			if (client.Session.Character is { } character)
			{
				await _loop.Run(() =>
				{
					_pvpMatchMaking.Forget(character.Id);
					_pvpMatches.Forget(character.Id);
					_groupFinder.Forget(character.Id);

					// Same rule for the LFG advert: the team search answers from live sessions, so a flag left behind
					// by a logged-off character is a row on somebody else's wire for a player who is not there. The
					// team's own advert needs no cleanup - the team dissolves with its last member.
					_teams.ForgetLookingForTeam(character.Id);
					return Task.CompletedTask;
				});
			}

			await base.ClientDisconnected(client);
		}

		public override async Task StopAsync(CancellationToken cancellationToken)
		{
			_csPlayerAgentDispatcher.Unregister(this);
			await base.StopAsync(cancellationToken);
			await _loop.StopAsync();
		}

		// --- ICsPlayerAgentProvider: the team ops that arrive on the player's world connection ------------------------
		// The world's calls that change this tier's state run on its loop, which a zone may wait on (see
		// CsPlayerAgentServices.LoopKey). The reads stay direct: they return a snapshot of state the loop replaces rather
		// than edits in place.
		public Task<bool> LeaveTeam(uint characterId)
		{
			return _loop.Run(() => _teams.Leave(characterId));
		}

		public Task<bool> KickFromTeam(uint leaderId, uint targetId)
		{
			return _loop.Run(() => _teams.Kick(leaderId, targetId));
		}

		public IReadOnlyList<uint> TeamMembersOf(uint characterId)
		{
			return _teams.TeamOf(characterId) is { } teamId
				? _teams.MembersOf(teamId)
				: [];
		}

		// The team's identity, which is what a team-owned playfield instance is keyed by.
		public uint? TeamIdOf(uint characterId)
		{
			return _teams.TeamOf(characterId);
		}

		// The started custom match holding this character on this map - what routes a minigame entry to the
		// match-owned instance and tells the entering client the match clock.
		public PvpMatchWorldEntry? PvpMatchEntryOf(uint characterId, uint mapId)
		{
			return _pvpMatches.PvpMatchEntryOf(characterId, mapId);
		}

		// The GM `.matchstart` override: form a match from the queue as it stands, however few are in it.
		public Task<string> ForcePvpMatchStart()
		{
			return _loop.Run(() => _pvpMatches.ForceFormFromQueue(_pvpMatchMaking.Queued(), _pvpMatchMaking.Forget));
		}

		// The GM `.matchstop` override: end the caller's started match now.
		public Task<string> ForcePvpMatchStop(uint characterId, string message)
		{
			return _loop.Run(() => _pvpMatches.ForceStopMatch(characterId, message));
		}

		// The PvP gate the world tier asks before letting a swing land on another player. The match state is here, so
		// the answer is.
		public bool ArePvpMatchOpponents(uint attackerId, uint victimId)
		{
			return _pvpMatches.ArePvpMatchOpponents(attackerId, victimId);
		}

		// And the kill that follows from it: a point for the killer, and possibly the end of the match.
		public Task CreditPvpMatchKill(uint killerId, uint victimId)
		{
			return _loop.Run(() => _pvpMatches.CreditKill(killerId, victimId));
		}

		// The guild bank's deposit half - the world tier has already taken the money out of the wallet.
		public Task<bool> DepositToGuildBank(uint characterId, ulong amountTin)
		{
			return _loop.Run(() => _guild.DepositToGuildBank(characterId, amountTin));
		}

		// "You have mail". Rides CSPlayerInterface rather than the tradepost interface, which is what lets it reach a
		// player who is nowhere near a mailbox - the mailbox itself is polled, so without this a player only learns
		// about mail by opening one. The sender is the same guild/team server object every other CS push uses.
		public async Task<bool> NotifyMailReceived(uint recipientId, ulong mailId)
		{
			if (ClientForCharacter(recipientId) is not { } client)
				return false;

			// The receiver is the recipient's own CSPlayerInterface instance - its character id, never instance 0. A
			// push addressed to instance 0 arrives and is dropped (see TeamService.Send).
			await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.CSPlayerAgentType, 0x04),
				client.Endpoint(RpcInterfaces.CSPlayerInterfaceType), CSPlayerInterfaceMethods.DispatchYouGotMailNotification,
				MailNotificationCodec.Build(mailId)));

			return true;
		}

		// Every packet runs on this tier's loop, one at a time with every other CS client's, its timers and the world's
		// calls into it.
		protected override Task ReceivedPacket(INetworkClient client, Rpc packet)
		{
			return _loop.Run(() => Dispatch(client, packet));
		}

		private async Task Dispatch(INetworkClient client, Rpc packet)
		{
			var method = (CSPlayerAgentMethods)packet.Method;
			LogPacketReceived(packet);

			// One reader over the body for the whole dispatch: BodyReader() hands out a fresh cursor per call, so the
			// handlers must share this instance rather than each re-reading from position 0. Cases that read inline use
			// it directly; those that hand off to a GuildService handler pass it in.
			var body = packet.BodyReader();

			switch (method)
			{
				case CSPlayerAgentMethods.Authenticate:
				{
					var characterId = body.ReadUInt32();
					var accountId = body.ReadUInt32();
					var authReply = body.ReadUInt32();

					if (await Database.GetAccount(accountId) is not {} account)
						throw new Exception("Account == null");

					client.Session.Account = account;
					client.Session.Account.ClientInstance = characterId;

					// No `!` here on purpose: GetCharacterForAccount returns null for a character this account does not own,
					// and forgiving that makes the check below look like dead code someone will helpfully delete.
					client.Session.Character = await Database.GetCharacterForAccount(accountId, characterId)
						?? throw new Exception($"Character not found for account {accountId:X} and client instance {characterId:X}");

					await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchAuthenticateAck, b => b.WriteUInt32(authReply)));

					// Guilded character: retail pushes the 0x203F full guild update right after the
					// Authenticate ack (claim_login_reward dump), before the 0x20D0/0x20C5 pair.
					await _guild.SendGuildStateOnLogin(client, packet);

					// The featured-activity list, empty - the frame word is ListFrame.ForCount(0), an empty list rather
					// than a terminator constant. All 135 captured frames are 4 bytes, so retail's own list was empty
					// in every one of them too.
					await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceMethods.DispatchGroupFinderUpdateFeaturedActivities,
						b => b.WriteUInt32(ListFrame.ForCount(0))));

					// The login push: "you are not in the finder", reason `GroupFinderCharacterLeftPlayfield`, silent = 1.
					// Retail sends it on every Authenticate handshake. The spelling (and its LDB key) lives in the codec.
					await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, GroupFinderRemovedCodec.Method,
						GroupFinderRemovedCodec.Build(GroupFinderRemovalReasons.CharacterLeftPlayfield(true))));

					break;
				}

				// 0x2003 TeamInvite(const ID32& i_cTargetID, const std::string& i_cMessage, bool i_bCheckCriminalStatus):
				// the leader (or a solo player) invites someone into a team. Body is 11 B in all 4 captures - the ID32,
				// an empty message and the flag.
				case CSPlayerAgentMethods.TeamInvite:
				{
					var invite = TeamInviteRequestCodec.Parse(body.ReadRemainingArray());
					var targetId = invite.Target.Id;
					var inviteMessage = invite.Message;
					var checkCriminalStatus = invite.CheckCriminalStatus;

					if (await Database.GetCharacter(targetId) is { } target)
						// The message rides the s2c push: DispatchTeamInvite's trailing "zero u16" is that string's
						// empty encoding (see TeamInviteCodec).
						await _teams.Invite(client, client.Session.Character, target, inviteMessage);
					else
						Logger.LogWarning("TeamInvite for unknown character {TargetId:X8}.", targetId);

					// i_bCheckCriminalStatus asks for the invite to be refused when the target is flagged criminal - a
					// PvP rule nothing here models, so there is no status to check and refusing would be inventing one.
					// Set in every captured invite, so this is not a rare path; it is a no-op until PvP flagging
					// exists.
					if (checkCriminalStatus)
						Logger.LogDebug("TeamInvite asked for a criminal-status check; no PvP flagging is modelled, so the invite proceeds.");

					break;
				}

				// 0x2008 AcceptTeamInvite([ID32 inviter]): the invitee joins. The inviter is taken from the server's
				// pending invite rather than the body, so a forged body cannot pull someone into a team they were
				// never invited to.
				case CSPlayerAgentMethods.AcceptTeamInvite:
				{
					EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)); // the inviter, echoed by the client
					await _teams.AcceptInvite(client.Session.Character.Id);
					break;
				}

				// 0x2009 DeclineTeamInvite: drop the pending invite. Nothing goes on the wire in the capture.
				case CSPlayerAgentMethods.DeclineTeamInvite:
				{
					_teams.Decline(client.Session.Character.Id);
					break;
				}

				// --- Looking for group ----------------------------------------------------------------------------
				// The four c2s methods that feed the team search, and the apply button on one of its rows. None of
				// them appears in the corpus - no traced session opened the LFG window - so each body below is the
				// client's own signature (docs/re/aoc-client-rpc-methods.json) and its reader. [V0]

				// 0x2005 TeamApply(const ID32& i_cTeamID): "apply" on a team-search row. The applicant does not join;
				// the leader is told, and invites them through the ordinary invite flow.
				case CSPlayerAgentMethods.TeamApply:
				{
					var applyTo = EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length));

					await _teams.Apply(client.Session.Character.Id, applyTo.Id);
					break;
				}

				// 0x2012 SetLookingForTeamStatus(bool i_bLookingForTeam, const std::string& i_cMessage) /
				// 0x2014 GetLookingForTeamStatus(): the character's own LFG flag. Both answer with
				// DispatchCharacterLookingForTeamStatus, which is the flag echoed back - the group finder already
				// sends that message when it clears the flag.
				case CSPlayerAgentMethods.SetLookingForTeamStatus:
				{
					var wanted = LookingForGroupStatusCodec.Parse(body.ReadRemainingArray());

					await SendLookingForGroupStatus(client, packet, CSPlayerInterfaceMethods.DispatchCharacterLookingForTeamStatus,
						_teams.SetLookingForTeam(client.Session.Character.Id, wanted.Looking, wanted.Message));
					break;
				}

				case CSPlayerAgentMethods.GetLookingForTeamStatus:
				{
					await SendLookingForGroupStatus(client, packet, CSPlayerInterfaceMethods.DispatchCharacterLookingForTeamStatus,
						_teams.LookingForTeamStatusOf(client.Session.Character.Id));
					break;
				}

				// 0x2013 SetLookingForMemberStatus(bool i_bLookingForMember, const std::string& i_cMessage) /
				// 0x2015 GetLookingForMemberStatus(): the team's advert - the flag that puts it in everyone else's
				// SearchForTeam results, and the message its result row carries. Only the leader may set it; a
				// non-leader (or a player in no team) is told nothing, which is what the null says.
				//
				// The set echoes to the whole team rather than just the leader: every member's window shows the
				// advert, and the team is the message's subject. Retail's own fan-out is uncaptured, so that is a
				// reading rather than an observation. [V0]
				case CSPlayerAgentMethods.SetLookingForMemberStatus:
				{
					var advert = LookingForGroupStatusCodec.Parse(body.ReadRemainingArray());

					if (_teams.SetLookingForMembers(client.Session.Character.Id, advert.Looking, advert.Message) is { } set)
						await _teams.BroadcastToTeam(client.Session.Character.Id, CSPlayerInterfaceMethods.DispatchTeamLookingForMemberStatus, LookingForGroupStatusCodec.Build(set));

					break;
				}

				case CSPlayerAgentMethods.GetLookingForMemberStatus:
				{
					if (_teams.LookingForMemberStatusOf(client.Session.Character.Id) is { } status)
						await SendLookingForGroupStatus(client, packet, CSPlayerInterfaceMethods.DispatchTeamLookingForMemberStatus, status);

					break;
				}

				// --- Group finder ---------------------------------------------------------------------------------
				// The signup flow chat-annotated-4 walks end to end; the service holds the trace and the gate.

				// 0x2082 RequestNeededRole(uint32 activityId): the client selected an activity in the finder window.
				case CSPlayerAgentMethods.RequestNeededRole:
				{
					await GroupFinderService.HandleRequestNeededRole(client, body.ReadUInt32());
					break;
				}

				// 0x2083 RequestRotatingActivityChildInfo(uint32 activityId): which child of a rotating activity is
				// running now. The corpus's only requests ask about activity 1.
				case CSPlayerAgentMethods.RequestRotatingActivityChildInfo:
				{
					await _groupFinder.HandleRequestRotatingActivityChildInfo(client, body.ReadUInt32());
					break;
				}

				// 0x207A QueueGroupFinder(const std::vector<uint32>& activites, bool wantsLeader, bool privateTeam):
				// the signup itself - where the quest-log gate refuses or the role select opens.
				case CSPlayerAgentMethods.QueueGroupFinder:
				{
					await _groupFinder.HandleQueue(client, GroupFinderQueueCodec.Parse(body.ReadRemainingArray()));
					break;
				}

				// 0x207B SelectGroupFinderRoles(uint32 roles): the role bitmask picked in groupFinder_roleSelect.
				case CSPlayerAgentMethods.SelectGroupFinderRoles:
				{
					await _groupFinder.HandleSelectRoles(client, body.ReadUInt32());
					break;
				}

				// 0x207D ReadyGroupFinder(bool ready, bool isTimeout): the ready-check answer; a solo confirm forms
				// the private team and enters the instance.
				case CSPlayerAgentMethods.ReadyGroupFinder:
				{
					var ready = body.ReadByte() != 0;
					var isTimeout = body.ReadByte() != 0;
					await _groupFinder.HandleReady(client, ready, isTimeout);
					break;
				}

				// 0x207C LeaveGroupFinder(): withdraw from the queue. Retail answers it - two captures answer all three
				// withdrawals with DispatchGroupFinderRemoved carrying the LDB token `GroupFinderLeaderDequeued`,
				// ~0.3 s later. The in-instance leave is the zone-line path instead; that push rides the Authenticate
				// flow with the other token.
				case CSPlayerAgentMethods.LeaveGroupFinder:
				{
					await _groupFinder.HandleLeave(client);
					break;
				}

				// 0x2019 StartVoteRetreat(): no parameters, so the empty body is correct. Nothing may be read here: the
				// method occurs zero times across every c2s packet addressed to the CSPlayerAgent in the corpus (the
				// 8-byte [u32 0xC350][u32 clientInstance] body that fires at login belongs to 0x201E
				// ClientRequestGuildInfo(const ID32&), 141 occurrences), so reading words that are not there would
				// desync the body stream the moment a retreat vote was cast.
				case CSPlayerAgentMethods.StartVoteRetreat:
				{
					break;
				}

				// --- Raids ----------------------------------------------------------------------------------------
				// Fifteen c2s methods, no capture anywhere in the corpus, every parameter list the client's own. The
				// service says what each does; the codecs say why each body looks the way it does.
				case CSPlayerAgentMethods.RaidCreate:
				{
					await _raids.Create(client.Session.Character.Id);

					break;
				}

				case CSPlayerAgentMethods.RaidLeave:
				{
					await _raids.Leave(client.Session.Character.Id);

					break;
				}

				// RaidInviteTeam / RaidKickTeam name a team; the accept/decline pair name the team that invited.
				case CSPlayerAgentMethods.RaidInviteTeam:
				{
					await _raids.InviteTeam(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);

					break;
				}

				case CSPlayerAgentMethods.RaidKickTeam:
				{
					await _raids.KickTeam(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);

					break;
				}

				// The inviter rides the server's pending-invite table rather than the echoed body, the same way
				// AcceptTeamInvite does: a forged body must not be able to pull someone into a raid.
				case CSPlayerAgentMethods.RaidInviteTeamAccept:
				case CSPlayerAgentMethods.RaidAcceptInvite:
				{
					EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length));
					await _raids.AcceptInvite(client.Session.Character.Id);

					break;
				}

				case CSPlayerAgentMethods.RaidInviteTeamDecline:
				case CSPlayerAgentMethods.RaidDeclineInvite:
				{
					EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length));
					await _raids.Decline(client.Session.Character.Id);

					break;
				}

				case CSPlayerAgentMethods.RaidKickCharacter:
				{
					await _raids.KickCharacter(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);

					break;
				}

				case CSPlayerAgentMethods.RaidMoveTeamMember:
				{
					var edit = RaidRosterEditCodec.Parse(body.ReadRemainingArray());
					await _raids.MoveMember(client.Session.Character.Id, edit.Subject.Id, edit.Destination.Id);

					break;
				}

				case CSPlayerAgentMethods.RaidSwapCharacters:
				{
					var swap = RaidRosterEditCodec.Parse(body.ReadRemainingArray());
					await _raids.Swap(client.Session.Character.Id, swap.Subject.Id, swap.Destination.Id);

					break;
				}

				case CSPlayerAgentMethods.RaidInviteCharacter:
				{
					var invite = RaidInviteRequestCodec.Parse(body.ReadRemainingArray());
					if (await Database.GetCharacter(invite.Target.Id) is { } target)
						await _raids.Invite(client.Session.Character.Id, target, invite.Message);
					else
						Logger.LogWarning("RaidInviteCharacter for unknown character {TargetId:X8}.", invite.Target.Id);

					break;
				}

				// The one raid message that names a player the server may never have seen. A name that resolves to
				// nobody is silently dropped: the client's own "no such character" answer belongs to the guild invite
				// family (DispatchGuildInviteNoSuchName) and the raid family has no counterpart, so sending one would
				// be inventing a message.
				case CSPlayerAgentMethods.RaidInviteCharacterByName:
				{
					var byName = RaidInviteByNameRequestCodec.Parse(body.ReadRemainingArray());
					if (await Database.GetCharacterByName(byName.CharacterName, client.Session.Character.RealmId) is { } named)
						await _raids.Invite(client.Session.Character.Id, named, byName.Message);
					else
						Logger.LogInformation("RaidInviteCharacterByName for unknown character \"{Name}\".", byName.CharacterName);

					break;
				}

				// The same three-field body TeamSetLootOption carries, which is captured.
				case CSPlayerAgentMethods.RaidSetLootOption:
				{
					var loot = TeamLootOptionCodec.Parse(body.ReadRemainingArray());
					await _raids.SetLootOption(client.Session.Character.Id, loot.Mode, loot.UseNeedGreed, loot.Threshold);

					break;
				}

				case CSPlayerAgentMethods.RaidSetMasterLooter:
				{
					await _raids.SetMasterLooter(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);

					break;
				}

				case CSPlayerAgentMethods.GuildGovernmentForm:
				{

					break;
				}

				case CSPlayerAgentMethods.AddGuildRenown:
				{

					break;
				}

				case CSPlayerAgentMethods.RemoveGuildRenown:
				{

					break;
				}

				case CSPlayerAgentMethods.ClientRequestGuildInfo:
				{
					await _guild.HandleClientRequestGuildInfo(client, packet, body);

					break;
				}

				// 0x201F ClientRequestGuildName([ID32 guild]): resolve a guild id to its display name.
				case CSPlayerAgentMethods.ClientRequestGuildName:
				{
					await _guild.HandleClientRequestGuildName(client, packet, body);

					break;
				}

				// 0x2006 TeamSetLootOption(int i_eLootMode, bool i_bUseNeedGreed, int i_nNewLootThreshold) - the leader
				// changes the group's loot rules. Echoed to every member as DispatchTeamLootOptionChanged.
				case CSPlayerAgentMethods.TeamSetLootOption:
				{
					var loot = TeamLootOptionCodec.Parse(body.ReadRemainingArray());

					if (!await _teams.SetLootOption(client.Session.Character.Id, loot.Mode, loot.UseNeedGreed, loot.Threshold))
						Logger.LogDebug("TeamSetLootOption from {Character:X8} with no team; ignored.", client.Session.Character.Id);

					break;
				}

				case CSPlayerAgentMethods.AcceptGuildInvitation:
				{
					await _guild.HandleAcceptGuildInvitation(client, packet, body);

					break;
				}

				case CSPlayerAgentMethods.DeclineGuildInvitation:
				{
					var characterId = client.Session.Character?.Id ?? 0;
					var declinedGuild = await _guild.DeclineGuildInvitation(characterId, client.Session.Character?.Name);
					if (declinedGuild != null)
						Logger.LogInformation("Guild invite to \"{Guild}\" declined by {CharacterId:X8}.", declinedGuild, characterId);

					break;
				}

				case CSPlayerAgentMethods.CreateGuild:
				{
					await _guild.HandleCreateGuild(client, packet, body);

					break;
				}

				case CSPlayerAgentMethods.GuildInviteByName:
				{
					await _guild.HandleGuildInviteByName(client, packet, body);

					break;
				}

				case CSPlayerAgentMethods.GuildInvite:
				{
					await _guild.HandleGuildInvite(client, packet, body);

					break;
				}

				case CSPlayerAgentMethods.GuildLeave:
				{
					await _guild.HandleGuildLeave(client);

					break;
				}

				// 0x2027 GuildKick / 0x2025 GuildPromote / 0x2026 GuildDemote: the three leader-only roster actions.
				// Each takes the target's ID32 and nothing else; the fan-out they cause is the service's.
				case CSPlayerAgentMethods.GuildKick:
				{
					await _guild.HandleGuildKick(client, body);

					break;
				}

				case CSPlayerAgentMethods.GuildPromote:
				case CSPlayerAgentMethods.GuildDemote:
				{
					await _guild.HandleGuildRankChange(client, body, method == CSPlayerAgentMethods.GuildPromote);

					break;
				}

				case CSPlayerAgentMethods.GuildWithdrawMoney:
				{
					await _guild.HandleGuildWithdrawMoney(client, body);

					break;
				}

				case CSPlayerAgentMethods.GuildMessageOfTheDay:
				{
					await _guild.HandleGuildMessageOfTheDay(client, packet, body);

					break;
				}

				case CSPlayerAgentMethods.MassivePvpInfoRequest:
				{
					// The client polls the Massive-PvP guild leaderboard with 0x2077; retail answers each with a 0x20A5
					// DispatchMassivePvpInfoRequestReturn (verified in the login dump: 0x2077 -> 0x20A5).
					await SendMassivePvpInfo(client, packet);

					break;
				}

				case CSPlayerAgentMethods.GuildRequestMemberList:
				{
					// Retail answers the member-list request with a fresh 0x203F full update plus the 0x20A5 leaderboard.
					await _guild.SendGuildStateOnLogin(client, packet);
					await SendMassivePvpInfo(client, packet);

					break;
				}

				case CSPlayerAgentMethods.RequestRenownHistory:
				{
					// The renown-over-time graph in the guild window (retail answers with 0x2039). No history is
					// tracked, so the reply is a zeroed block - a flat graph.
					await SendGuildRenownHistory(client, packet);

					break;
				}

				case CSPlayerAgentMethods.SearchForGuilds:
				{
					// SearchForGuilds(uint16 i_nMinMembers, uint16 i_nMaxMembers, uint8 i_nMinLevel, uint8 i_nMaxLevel,
					// const String& i_cNameSearch) - the guild-finder browse window (0x2043 -> 0x205C). The 8-byte
					// captured body is exactly that, with an empty name. The criteria are applied - every captured
					// search sends the wide-open defaults, so filtering is invisible in the corpus but not in the
					// client.
					var minMembers = body.ReadUInt16();
					var maxMembers = body.ReadUInt16();
					var minLevel = body.ReadByte();
					var maxLevel = body.ReadByte();
					var nameSearch = body.ReadString();

					await SendGuildSearchResult(client, packet, minMembers, maxMembers, minLevel, maxLevel, nameSearch);

					break;
				}

				case CSPlayerAgentMethods.CharacterSearch:
				{
					// CharacterSearch(int i_eSearchmode, const InstanceVec_t& i_cPlayfieldVec, int i_nMinLevel,
					// int i_nMaxLevel, const IntVec_t& i_nClassVec, const IntVec_t& i_nRaceVec, uint32 i_nSkipResults,
					// const String& i_cNameSearch) - the "who" window (0x200A -> 0x2010). The captured 30-byte body is
					// that exactly: mode 1, three empty ListFrame vectors, levels 5..10, skip 0, empty name. Answered
					// from the online players, filtered by every criterion - this is a /who, not a database browse.
					var searchMode = body.ReadUInt32();
					var playfields = ReadListFrameOfUInt32(body, "CharacterSearch.playfields");
					var minLevel = body.ReadUInt32();
					var maxLevel = body.ReadUInt32();
					var classes = ReadListFrameOfUInt32<ConanClass>(body, "CharacterSearch.classes");
					var races = ReadListFrameOfUInt32<Race>(body, "CharacterSearch.races");
					var skipResults = body.ReadUInt32();
					var nameSearch = body.ReadString();

					Logger.LogDebug("CharacterSearch mode {Mode}, level {MinLevel}..{MaxLevel}, {Playfields} playfields / {Classes} classes / {Races} races, skip {Skip}, name '{Name}'.", searchMode, minLevel, maxLevel,
						playfields.Count, classes.Count, races.Count, skipResults, nameSearch);

					await SendCharacterSearchResult(client, packet, minLevel, maxLevel, playfields, classes, races, skipResults, nameSearch);

					break;
				}

				case CSPlayerAgentMethods.SearchForTeam:
				{
					// SearchForTeam(const InstanceVec_t& i_cPlayfieldVec, int i_nMinLevel, int i_nMaxLevel,
					// int memberCountMin, int memberCountMax, uint32 i_nSkipResults) - LFG team search
					// (0x200B -> 0x200F). Captured 24-byte body matches exactly.
					var playfields = ReadListFrameOfUInt32(body, "SearchForTeam.playfields");
					var minLevel = body.ReadUInt32();
					var maxLevel = body.ReadUInt32();
					var memberCountMin = body.ReadUInt32();
					var memberCountMax = body.ReadUInt32();
					var skipResults = body.ReadUInt32();

					// Answered from the LFG registry: the live teams whose leader flagged them with
					// SetLookingForMemberStatus, narrowed by every criterion the client sent.
					Logger.LogDebug("SearchForTeam level {MinLevel}..{MaxLevel}, members {MinMembers}..{MaxMembers}, {Playfields} playfields, skip {Skip}.", minLevel, maxLevel,
						memberCountMin, memberCountMax, playfields.Count, skipResults);

					await SendTeamSearchResult(client, packet, playfields, minLevel, maxLevel, memberCountMin, memberCountMax, skipResults);

					break;
				}

				// --- PvP minigame queue ---------------------------------------------------------------------------
				// The four c2s messages the corpus carries, and the two live replies they get. The board is live state
				// - see PvpMatchMakingService.

				// 0x2052 PvPMatchMakingPutIntoMatchMaking(map, type, bringTeam, queueType): sign up for a minigame.
				case CSPlayerAgentMethods.PvPMatchMakingPutIntoMatchMaking:
				{
					await _pvpMatchMaking.HandlePutIntoMatchMaking(client, PvpMatchMakingSignupCodec.Parse(body.ReadRemainingArray()));
					// Each new signup runs the formation pass - two compatible signups become a started match, exactly
					// as the group finder's product is a team.
					await _pvpMatches.TryFormFromQueue(_pvpMatchMaking.Queued(), _pvpMatchMaking.Forget);
					break;
				}

				// 0x2053 PvPMatchMakingRemoveFromMatchMaking(): withdraw. No argument - a character holds one signup.
				case CSPlayerAgentMethods.PvPMatchMakingRemoveFromMatchMaking:
				{
					await _pvpMatchMaking.HandleRemoveFromMatchMaking(client);
					break;
				}

				// 0x2054 PvPMatchMakingRequestInfo(int i_QueueType): the board poll, every ~5 s while the minigame
				// window is open.
				case CSPlayerAgentMethods.PvPMatchMakingRequestInfo:
				{
					await _pvpMatchMaking.HandleRequestInfo(client, body.ReadUInt32());
					break;
				}

				case CSPlayerAgentMethods.PvPMatchMakingIsInMatchMaking:
				{
					// "Am I queued for PvP matchmaking?" (0x2055). Retail sends no response - not even in the capture
					// where the player was queued, which is what makes the silence the answer rather than a gap. The
					// client learns its own state from the 0x2092 board it polls anyway. Consumed so it is not logged
					// as unhandled.
					break;
				}

				// --- Custom PvP matches ---------------------------------------------------------------------------
				// The create/join/side/start lifecycle - the message set is recovered from the client and uncaptured
				// end to end (no captured session ever creates a match): see PvpMatchService. The c2s bodies below are
				// the client's own parameter lists (docs/re/aoc-client-rpc-methods.json); an `ID32::InstanceType` is
				// one u32 (the instance word alone), a `const ID32&` two.

				// 0x204A PvPMatchMakingCreateMatch(const PvPMatchRules_c&): the whole body is the ruleset element.
				case CSPlayerAgentMethods.PvPMatchMakingCreateMatch:
				{
					await _pvpMatches.HandleCreateMatch(client, PvpMatchRulesCodec.Parse(body.ReadRemainingArray()));
					break;
				}

				// 0x204B PvPMatchMakingJoinMatch(ID32::InstanceType i_nMatchID, bool i_bBringTeam, string i_cPassword).
				case CSPlayerAgentMethods.PvPMatchMakingJoinMatch:
				{
					var matchId = body.ReadUInt32();
					var bringTeam = body.ReadByte() != 0;
					await _pvpMatches.HandleJoinMatch(client, matchId, bringTeam, body.ReadString());
					break;
				}

				// 0x204C PvPMatchMakingSetSide(int i_nSide).
				case CSPlayerAgentMethods.PvPMatchMakingSetSide:
				{
					await _pvpMatches.HandleSetSide(client, body.ReadUInt32());
					break;
				}

				// 0x204D PvPMatchMakingMatchSay(string i_cMessage).
				case CSPlayerAgentMethods.PvPMatchMakingMatchSay:
				{
					await _pvpMatches.HandleMatchSay(client, body.ReadString());
					break;
				}

				// 0x204E PvPMatchMakingLeaveMatch().
				case CSPlayerAgentMethods.PvPMatchMakingLeaveMatch:
				{
					await _pvpMatches.HandleLeaveMatch(client);
					break;
				}

				// 0x2050 PvPMatchMakingGetMatchInfo(ID32::InstanceType i_nMatchID).
				case CSPlayerAgentMethods.PvPMatchMakingGetMatchInfo:
				{
					await _pvpMatches.HandleGetMatchInfo(client, body.ReadUInt32());
					break;
				}

				// 0x2051 PvPMatchMakingTeleportToMinigame(): the member asks to be moved into the minigame.
				case CSPlayerAgentMethods.PvPMatchMakingTeleportToMinigame:
				{
					await _pvpMatches.HandleTeleportToMinigame(client);
					break;
				}

				// 0x2056 PvPMatchMakingKickPlayer(const ID32&, string) / 0x2057 ...BanPlayer(const ID32&, string).
				case CSPlayerAgentMethods.PvPMatchMakingKickPlayer:
				case CSPlayerAgentMethods.PvPMatchMakingBanPlayer:
				{
					body.ReadUInt32(); // the ID32's entity-class word
					var targetId = body.ReadUInt32();
					await _pvpMatches.HandleKickPlayer(client, targetId, body.ReadString(), packet.Method == (uint)CSPlayerAgentMethods.PvPMatchMakingBanPlayer);
					break;
				}

				// 0x2058 PvPMatchMakingMakePlayerMatchOwner(const ID32&).
				case CSPlayerAgentMethods.PvPMatchMakingMakePlayerMatchOwner:
				{
					body.ReadUInt32(); // entity class
					await _pvpMatches.HandleMakePlayerMatchOwner(client, body.ReadUInt32());
					break;
				}

				// 0x2059 PvPMatchMakingForceSide(const ID32&, int i_nSide).
				case CSPlayerAgentMethods.PvPMatchMakingForceSide:
				{
					body.ReadUInt32(); // entity class
					var forcedId = body.ReadUInt32();
					await _pvpMatches.HandleForceSide(client, forcedId, body.ReadUInt32());
					break;
				}

				// 0x205A PvPMatchMakingStartMatch().
				case CSPlayerAgentMethods.PvPMatchMakingStartMatch:
				{
					await _pvpMatches.HandleStartMatch(client);
					break;
				}

				case CSPlayerAgentMethods.EchoGroupFinderRemoved:
				{
					// The client echoes a GroupFinder remoteformat back (e.g. token=
					// "GroupFinderCharacterLeftPlayfield") - a notification the server consumes; no response is needed,
					// retail pushes its own 0x20C5 downloads separately. Consumed so it is not logged as unhandled.
					break;
				}

				case CSPlayerAgentMethods.EchoGroupFinderUpdateFeaturedActivities:
				{
					// The echoed body is the activities list itself, and 0x000003F1 is a ListFrame count word rather
					// than a constant: the client hands back the empty list (count 0) it was sent.
					var word = body.ReadUInt32();
					if (ListFrame.TryCountOf(word, out var activityCount))
						Logger.LogDebug("EchoGroupFinderUpdateFeaturedActivities: {Count} featured activities echoed back.", activityCount);
					else
						Logger.LogWarning("EchoGroupFinderUpdateFeaturedActivities: 0x{Word:X} is not a count-framed list word.", word);

					break;
				}

				case CSPlayerAgentMethods.RemovePvpPoints:
				{
					var data = body.ReadRemainingArray();

					// The body carries a guild list - id, name and counters, the names plainly readable as Latin-1 text.
					// It is not decoded here and nothing reads `data`.

					break;
				}

				case CSPlayerAgentMethods.RequestRenownRankings:
				{
					await SendGuildList(client, packet);

					break;
				}

				// 0x2042 RequestBattleKeepInformation(const InstanceVec_t&): the Border Kingdoms siege panel queries
				// keep status (retail answers each with a 0x20AA DispatchBattleKeepInfoRequestReturnPhase3And4 -
				// NecroL80_mage-necromancer_login_gameplay_annotated_9, four pairs). Captured vectors carry one keep id
				// each.
				case CSPlayerAgentMethods.RequestBattleKeepInformation:
				{
					var keepIds = ReadListFrameOfUInt32(body, "RequestBattleKeepInformation");
					await SendBattleKeepInfo(client, packet, keepIds);

					break;
				}

				default:
				{
					LogUnknownPacket(packet);
					break;
				}
			}
		}

		// A count-framed vector of u32s (`InstanceVec_t` / `IntVec_t` in the client's search signatures): the ListFrame
		// count word then that many words, returned as elements because the search handlers filter on them. Every
		// captured search vector is empty, so the element width is inferred from the C++ type (both vectors are of
		// 4-byte elements) rather than observed; a malformed word is rejected instead of looping on inbound arithmetic.
		private List<uint> ReadListFrameOfUInt32(FaolanStream body, string what)
		{
			var word = body.ReadUInt32();
			if (!ListFrame.TryCountOf(word, out var count))
			{
				Logger.LogWarning("{What}: 0x{Word:X} is not a count-framed list word; ignoring the entries.", what, word);
				return [];
			}

			var values = new List<uint>(count);
			for (var i = 0; i < count; i++)
				values.Add(body.ReadUInt32());

			return values;
		}

		private List<T> ReadListFrameOfUInt32<T>(FaolanStream body, string what) where T: Enum
		{
			var word = body.ReadUInt32();
			if (!ListFrame.TryCountOf(word, out var count))
			{
				Logger.LogWarning("{What}: 0x{Word:X} is not a count-framed list word; ignoring the entries.", what, word);
				return [];
			}

			var values = new List<T>(count);
			for (var i = 0; i < count; i++)
				values.Add(body.ReadUInt32<T>());

			return values;
		}
	}
}
