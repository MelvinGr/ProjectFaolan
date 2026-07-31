using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.CSPlayerAgent
{
	public partial class CsPlayerAgentListener
	{
		// 0x2038 DispatchGuildRankingsUpdate: the global guild-renown leaderboard (the "Guild Renown Rankings" window,
		// tabs Total/Valor/Glory/Artistry). Built from the realm's guilds via GuildRankingsCodec (byte-exact,
		// GuildRankingsTests) - no longer the captured retail leaderboard. A guild appears on a tab only once it has
		// renown in that category, so a fresh server shows an empty board rather than fake guilds.
		private async Task SendGuildList(INetworkClient client, Rpc packet)
		{
			var realmId = client.Session.Character?.RealmId ?? 0;
			var guilds = await Database.GetGuilds(realmId);

			var rankings = GuildRankings.FromGuilds(
				guilds.Select(g => new GuildRankings.Row(g.Id, g.Name ?? "", g.RenownValor, g.RenownGlory, g.RenownArtistry, (uint)(g.Members?.Count ?? 0), g.Level)));

			await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildRankingsCodec.Opcode, GuildRankingsCodec.Build(rankings)));
		}

		// 0x205C DispatchGuildSearchResult: the guild-finder browse list (client requests it with 0x2043
		// SearchForGuilds). Built from the realm's guilds via GuildSearchResultCodec (byte-exact,
		// GuildSearchResultTests); the leader is the highest-ranked member.
		//
		// The 0x2043 criteria are APPLIED (#460). Every captured search sends the wide-open defaults - the browse
		// window opens with the sliders at their limits and an empty name - so no capture distinguishes a filtered
		// result from an unfiltered one, and the corpus cannot be the oracle here. The filter is therefore written to
		// the client's own signature (`uint16 i_nMinMembers, uint16 i_nMaxMembers, uint8 i_nMinLevel,
		// uint8 i_nMaxLevel, String i_cNameSearch`) and to what the widget plainly means, with the one rule that a
		// ZERO bound is "no bound" rather than a real limit - otherwise the defaults would filter everything out.
		private async Task SendGuildSearchResult(INetworkClient client, Rpc packet, ushort minMembers = 0, ushort maxMembers = 0, byte minLevel = 0, byte maxLevel = 0, string nameSearch = "")
		{
			var realmId = client.Session.Character?.RealmId ?? 0;
			var guilds = await Database.GetGuilds(realmId);

			var matches = guilds
				.Where(g => Within(g.Members?.Count ?? 0, minMembers, maxMembers))
				.Where(g => Within((int)g.Level, minLevel, maxLevel))
				.Where(g => nameSearch.Length == 0 || (g.Name ?? "").Contains(nameSearch, StringComparison.OrdinalIgnoreCase))
				.ToList();

			if (matches.Count != guilds.Length)
				Logger.LogDebug("SearchForGuilds members {MinMembers}..{MaxMembers}, level {MinLevel}..{MaxLevel}, name '{Name}': {Matched} of {Total} guilds.", minMembers, maxMembers, minLevel, maxLevel,
					nameSearch, matches.Count, guilds.Length);

			var result = new GuildSearchResult(matches
				.Select(g => new GuildSearchEntry(g.Id, g.Name ?? "", g.Members?.OrderByDescending(m => m.GuildRank).FirstOrDefault()?.Name ?? "", (ushort)(g.Members?.Count ?? 0),
					(uint)g.RenownTotal, 0))
				.ToList());

			await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildSearchResultCodec.Opcode, GuildSearchResultCodec.Build(result)));
		}

		// An inclusive range where 0 means "unbounded" at either end. The search widgets send 0/0 when the player has
		// not touched the sliders, which is every captured search, so treating 0 as a hard bound would answer the
		// default browse with nothing at all.
		private static bool Within(int value, uint min, uint max)
		{
			return (min == 0 || value >= min) && (max == 0 || value <= max);
		}

		// 0x2010 DispatchCharacterSearchResult: the character/"who" search result (answer to 0x200A CharacterSearch),
		// built by CharacterSearchResultCodec (byte-exact against the captured 3-character body, CharacterSearchResultTests).
		//
		// Answered from the ONLINE players, because this is a /who and not a database browse: an offline character has
		// no playfield to report and the client greys members by playfield. GetWorldSessions() is the whole population,
		// so "everyone online in this realm" is the candidate set and every criterion narrows it (#460).
		//
		// The empty result still carries the frame AND the trailing 0x00, or the client reads the empty frame then runs
		// off the end - the #408 dropped-trailer failure that took the whole CSPlayerAgent route down.
		private Task SendCharacterSearchResult(INetworkClient client, Rpc packet, uint minLevel, uint maxLevel, List<uint> playfields, List<uint> classes, List<uint> races, uint skipResults,
			string nameSearch)
		{
			var realmId = client.Session.Character?.RealmId ?? 0;

			var matches = _gameServerDispatcher.GetWorldSessions()
				.Select(s => s.Character)
				.Where(c => c != null && c.RealmId == realmId)
				.Where(c => Within((int)c.Level, minLevel, maxLevel))
				.Where(c => classes.Count == 0 || classes.Contains((uint)c.Class))
				.Where(c => races.Count == 0 || races.Contains((uint)c.Race))
				.Where(c => nameSearch.Length == 0 || (c.Name ?? "").Contains(nameSearch, StringComparison.OrdinalIgnoreCase))
				.Select(c => (Character: c, MapId: _gameServerDispatcher.GetCharacterLiveState(c.Id)?.MapId ?? c.MapId))
				.Where(m => playfields.Count == 0 || playfields.Contains(m.MapId))
				.OrderBy(m => m.Character.Name, StringComparer.OrdinalIgnoreCase)
				.Skip((int)Math.Min(skipResults, int.MaxValue))
				.Take(MaxCharacterSearchResults)
				.Select(m => TeamMemberRecord.FromCharacter(m.Character, m.MapId))
				.ToList();

			Logger.LogDebug("CharacterSearch: {Count} online character(s) matched.", matches.Count);

			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CharacterSearchResultCodec.Opcode, CharacterSearchResultCodec.Build(new CharacterSearchResult(matches))));
		}

		// One page of "who" results. The client's own request carries i_nSkipResults, so it pages, and the reply method
		// is DispatchCharacterSearchResult(i_cResult, i_bMoreHits) - retail caps the page and says whether more exist.
		// The captured bodies carry 3 and 4 records, which sets no ceiling; 50 is ours until a capture shows retail's.
		// [V0]
		private const int MaxCharacterSearchResults = 50;

		// 0x2039 DispatchGuildRenownHistoryUpdate: the renown-over-time graph in the guild window (answer to 0x2040
		// RequestRenownHistory). The captured body is a fixed 112-byte block of renown/pvp time buckets; we track no
		// renown history, so we emit it zeroed (a flat graph) - the honest empty form for a guild with no history.
		private static Task SendGuildRenownHistory(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchGuildRenownHistoryUpdate, new byte[112]));
		}

		// 0x20A5 DispatchMassivePvpInfoRequestReturn: the Massive-PvP minigame guild leaderboard (the client polls it
		// with 0x2077, and it rides alongside the 0x203F on a member-list request). Grammar re-derived byte-exact from
		// every retail 0x20A5 capture (all show the top-8 board; the parse rebuilds each body 1:1):
		//   [u32 timestamp][u32 (guildCount+1)*0x3F1]  { guildCount × record }  [u32 guildCount+1]
		//   record = [u32 rank][u32 rank][u32 a][u32 b][u32 c][u32 scoreA][u32 scoreB][u32 tsUnix][u32 guildId]
		//            [u16 nameLen][name][7 zero bytes]
		// The TRAILING [u32 guildCount+1] is the fix for #408: we previously emitted only [timestamp][(0+1)*0x3F1]
		// and DROPPED that trailer, so the client read the timestamp (its i_nTimeRegionOffset field) + the empty list
		// frame, then ran off the end reading the trailer -> "stream bad after i_nTimeRegionOffset" -> it disconnected
		// the CSPlayerAgent route on every login. We run no Massive-PvP minigame, so we emit the honest EMPTY board:
		// [timestamp 0][(0+1)*0x3F1][0+1]. (No empty-list capture oracle exists - every retail board has 8 guilds - so
		// the empty form is inferred from the round-trip-verified grammar and confirmed in-client: the route no longer
		// drops.) Build the real records here if/when a minigame subsystem exists.
		private static Task SendMassivePvpInfo(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchMassivePvpInfoRequestReturn, (byte[])[0x01], b => b
				.WriteUInt32(0) // last-update timestamp (cosmetic; 0 = never updated)
				.WriteUInt32(ListFrame.ForCount(0)) // empty 0x3F1-framed guild list ((0+1)*0x3F1)
				.WriteUInt32(1))); // trailing guildCount+1 (= 1 for the empty board) - the field the client read past
		}

		// 0x2092 DispatchPvPMatchMakingInfoRequestReturn: PvP matchmaking info (answer to 0x2054
		// PvPMatchMakingRequestInfo). Retail's "not queued" body is [0x3F1][u32 0] (byte-exact from the login capture);
		// the header carries the same 0x01 extra byte as the capture.
		private static Task SendPvpMatchMakingInfo(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchPvPMatchMakingInfoRequestReturn, (byte[])[0x01], b => b
				.WriteUInt32(ListFrame.ForCount(0)) // empty 0x3F1-framed record list
				.WriteUInt32(0))); // trailer
		}

		// 0x200F DispatchTeamSearchResult: the LFG team-search result (answer to 0x200B SearchForTeam). Body is a
		// 0x3F1-framed list + a trailing byte; retail's own "no results" body is [0x3F1][0x00] (byte-exact from the
		// login capture, where Hoxxling's search returned nothing).
		private static Task SendTeamSearchResult(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchTeamSearchResult, b => b
				.WriteUInt32(ListFrame.ForCount(0)) // empty 0x3F1-framed record list
				.WriteByte(0x00))); // list trailer
		}
	}
}
