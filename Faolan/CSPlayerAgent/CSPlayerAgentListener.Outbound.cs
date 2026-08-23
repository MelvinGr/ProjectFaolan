using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.GroupFinder;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Pvp;
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

			await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildRankingsCodec.Method, GuildRankingsCodec.Build(rankings)));
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

			await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildSearchResultCodec.Method, GuildSearchResultCodec.Build(result)));
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
		private Task SendCharacterSearchResult(INetworkClient client, Rpc packet, uint minLevel, uint maxLevel, List<uint> playfields, List<ConanClass> classes, List<Race> races, uint skipResults,
			string nameSearch)
		{
			var realmId = client.Session.Character?.RealmId ?? 0;

			var matches = _gameServerDispatcher.GetWorldSessions()
				.Select(s => s.Character)
				.Where(c => c != null && c.RealmId == realmId)
				.Where(c => Within((int)c.Level, minLevel, maxLevel))
				.Where(c => classes.Count == 0 || classes.Contains(c.Class))
				.Where(c => races.Count == 0 || races.Contains(c.Race))
				.Where(c => nameSearch.Length == 0 || (c.Name ?? "").Contains(nameSearch, StringComparison.OrdinalIgnoreCase))
				.Select(c => (Character: c, MapId: _gameServerDispatcher.GetCharacterLiveState(c.Id)?.MapId ?? c.MapId))
				.Where(m => playfields.Count == 0 || playfields.Contains(m.MapId))
				.OrderBy(m => m.Character.Name, StringComparer.OrdinalIgnoreCase)
				.Skip((int)Math.Min(skipResults, int.MaxValue))
				.Take(MaxCharacterSearchResults + 1) // one past the page, so the page knows whether it is the last
				.Select(m => TeamMemberRecord.FromCharacter(m.Character, m.MapId))
				.ToList();

			// i_bMoreHits, which used to go out as a hardcoded 0 because the trailing byte was read as a list
			// terminator (#889): the page caps at MaxCharacterSearchResults, so a search that matched more than that
			// was told there was nothing more and would never page for the rest.
			var moreHits = matches.Count > MaxCharacterSearchResults;
			var page = moreHits ? matches[..MaxCharacterSearchResults] : matches;

			Logger.LogDebug("CharacterSearch: {Count} online character(s) matched{More}.", page.Count, moreHits ? " (more to come)" : "");

			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CharacterSearchResultCodec.Method,
				CharacterSearchResultCodec.Build(new CharacterSearchResult(page, moreHits))));
		}

		// One page of "who" results. The client's own request carries i_nSkipResults, so it pages, and the reply method
		// is DispatchCharacterSearchResult(i_cResult, i_bMoreHits) - retail caps the page and says whether more exist.
		// The captured bodies carry 3 and 4 records, which sets no ceiling; 50 is ours until a capture shows retail's.
		// [V0]
		private const int MaxCharacterSearchResults = 50;

		// 0x2039 DispatchGuildRenownHistoryUpdate: the renown-over-time figures in the guild window (answer to 0x2040
		// RequestRenownHistory). The 112-byte body is FOUR named entries, not a blob - GuildRenownHistoryCodec has the
		// decode (#104). We track no renown history, so the four go out empty; the difference from the `new byte[112]`
		// this replaced is that the zeros are now the codec's named zeros and the shape is asserted, so the day a
		// history exists it is a value change rather than a new message.
		private static Task SendGuildRenownHistory(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildRenownHistoryCodec.Method,
				GuildRenownHistoryCodec.Build(GuildRenownHistory.Empty)));
		}

		// 0x20A5 DispatchMassivePvpInfoRequestReturn: the Massive-PvP (Border Kingdom battlekeep) guild leaderboard -
		// the client polls it with 0x2077, and it rides alongside the 0x203F on a member-list request. The grammar,
		// including the trailing word that was the fix for #408, lives in MassivePvpInfoCodec; we run no Massive-PvP
		// layer, so the honest zero-guild board goes out. Build real rows there if/when the subsystem exists.
		private static Task SendMassivePvpInfo(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, MassivePvpInfoCodec.Method,
				MassivePvpInfoCodec.Build(MassivePvpInfo.Empty)));
		}

		// NB the 0x2092 DispatchPvPMatchMakingInfoRequestReturn sender that used to live here is GONE (#882). It
		// wrote the constant `[0x3F1][u32 0]` on the strength of the login captures, and the matchmaking capture
		// shows that body is the EMPTY BOARD rather than the message's shape: the real one is a three-level map that
		// moves with every signup. It is built by `PvpMatchMakingService` / `PvpMatchMakingInfoCodec` now.

		// 0x200F DispatchTeamSearchResult: the LFG team-search result (answer to 0x200B SearchForTeam) - one page of
		// the teams that are looking for members, each as a whole roster plus its advert.
		//
		// This used to write a hardcoded `[0x3F1][0x00]` - which IS retail's answer, but only to a search that matched
		// nothing, and it was the only answer we could give with no LFG registry to answer from. The registry lives on
		// TeamService (SetLookingForMemberStatus) and applies the criteria; TeamSearchResultCodec owns the grammar,
		// including the trailing byte, which the client's reader names i_bMoreHits rather than a list trailer (#889).
		// The empty page still goes out as those same five bytes, gated byte-exact by TeamSearchResultCodecTests.
		private Task SendTeamSearchResult(INetworkClient client, Rpc packet, List<uint> playfields, uint minLevel, uint maxLevel, uint memberCountMin, uint memberCountMax, uint skipResults)
		{
			var result = _teams.SearchTeams(client.Session.Character?.Id ?? 0, playfields, minLevel, maxLevel, memberCountMin, memberCountMax, skipResults);

			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, TeamSearchResultCodec.Method, TeamSearchResultCodec.Build(result)));
		}

		// The LFG status echo, for all four of the Set/Get pairs the client can ask: one flag and one message, and
		// WHICH subject it is about is carried by the method rather than the body (LookingForGroupStatusCodec).
		private static Task SendLookingForGroupStatus(INetworkClient client, Rpc packet, CSPlayerInterfaceMethods method, LookingForGroupStatus status)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, (uint)method, LookingForGroupStatusCodec.Build(status)));
		}
	}
}
