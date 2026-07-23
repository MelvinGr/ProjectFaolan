using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Protocol;

namespace Faolan.CSPlayerAgent
{
	public partial class CsPlayerAgentListener
	{
		// 0x2038 DispatchGuildRankingsUpdate: the global guild-renown leaderboard (the "Guild Renown Rankings" window,
		// tabs Total/Valor/Glory/Artistry). Built from the realm's guilds via GuildRankings (byte-exact codec,
		// GuildRankingsTests) - no longer the captured retail leaderboard. A guild appears on a tab only once it has
		// renown in that category, so a fresh server shows an empty board rather than fake guilds.
		private async Task SendGuildList(INetworkClient client, Rpc packet)
		{
			var realmId = client.Session.Character?.RealmId ?? 0;
			var guilds = await Database.GetGuilds(realmId);

			var rankings = GuildRankings.FromGuilds(
				guilds.Select(g => new GuildRankings.Row(g.Id, g.Name ?? "", g.RenownValor, g.RenownGlory, g.RenownArtistry, (uint)(g.Members?.Count ?? 0), g.Level)));

			await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildRankings.Opcode, rankings.Encode()));
		}

		// 0x205C DispatchGuildSearchResult: the guild-finder browse list (client requests it with 0x2043
		// SearchForGuilds). Built from the realm's guilds via GuildSearchResult (byte-exact codec,
		// GuildSearchResultTests). We ignore the 0x2043 level/name filter and return every guild in the realm; the
		// leader is the highest-ranked member.
		private async Task SendGuildSearchResult(INetworkClient client, Rpc packet)
		{
			var realmId = client.Session.Character?.RealmId ?? 0;
			var guilds = await Database.GetGuilds(realmId);

			var result = new GuildSearchResult
			{
				Guilds = guilds
					.Select(g => new GuildSearchResult.Entry(g.Id, g.Name ?? "", g.Members?.OrderByDescending(m => m.GuildRank).FirstOrDefault()?.Name ?? "", (ushort)(g.Members?.Count ?? 0),
						(uint)g.RenownTotal, 0))
					.ToList()
			};

			await client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildSearchResult.Opcode, result.Encode()));
		}

		// 0x2010 DispatchCharacterSearchResult: the character/"who" search result (answer to 0x200A CharacterSearch).
		// Grammar decoded byte-exact from the hoxxling login capture (3 records, rebuilt 1:1):
		//   [u32 (count+1)*0x3F1] { count × record } [u8 0x00]
		// The trailing [u8 0x00] closes the list (same end-marker shape as DispatchTeamSearchResult's 000003f100). We
		// keep no character-search index, so we return the empty list - but MUST still emit that trailer, or the client
		// reads the empty frame then runs off the end (the #408 dropped-trailer failure mode). Empty body = [(0+1)*0x3F1][0].
		private static Task SendCharacterSearchResult(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchCharacterSearchResult,
				body => body.WriteUInt32(GuildService.GuildListMultiplier) // empty 0x3F1-framed record list
					.WriteByte(0x00))); // list end-marker
		}

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
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchMassivePvpInfoRequestReturn,
				(byte[]) [0x01],
				body => body.WriteUInt32(0) // last-update timestamp (cosmetic; 0 = never updated)
					.WriteUInt32(GuildService.GuildListMultiplier) // empty 0x3F1-framed guild list ((0+1)*0x3F1)
					.WriteUInt32(1))); // trailing guildCount+1 (= 1 for the empty board) - the field the client read past
		}

		// 0x2092 DispatchPvPMatchMakingInfoRequestReturn: PvP matchmaking info (answer to 0x2054
		// PvPMatchMakingRequestInfo). Retail's "not queued" body is [0x3F1][u32 0] (byte-exact from the login capture);
		// the header carries the same 0x01 extra byte as the capture.
		private static Task SendPvpMatchMakingInfo(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchPvPMatchMakingInfoRequestReturn,
				(byte[]) [0x01],
				body => body.WriteUInt32(GuildService.GuildListMultiplier) // empty 0x3F1-framed record list
					.WriteUInt32(0))); // trailer
		}

		// 0x200F DispatchTeamSearchResult: the LFG team-search result (answer to 0x200B SearchForTeam). Body is a
		// 0x3F1-framed list + a trailing byte; retail's own "no results" body is [0x3F1][0x00] (byte-exact from the
		// login capture, where Hoxxling's search returned nothing).
		private static Task SendTeamSearchResult(INetworkClient client, Rpc packet)
		{
			return client.SendAsync(new Rpc(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchTeamSearchResult,
				body => body.WriteUInt32(GuildService.GuildListMultiplier) // empty 0x3F1-framed record list
					.WriteByte(0x00))); // list trailer
		}
	}
}
