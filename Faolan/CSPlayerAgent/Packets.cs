using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Conan;

namespace Faolan.CSPlayerAgent
{
	public partial class CsPlayerAgentListener
	{
		// The empty AoC count-framed-list word ((0 elements + 1) * 0x3F1) - what the client's list readers expect for
		// a zero-length list (see GuildService.GuildListMultiplier / the 0x3F1 framing).
		private const uint EmptyGuildList = GuildService.GuildListMultiplier;

		// 0x2038 DispatchGuildRankingsUpdate: the global guild-renown leaderboard (the "Guild Renown Rankings" window,
		// tabs Total/Valor/Glory/Artistry). Built from the realm's guilds via GuildRankings (byte-exact codec,
		// GuildRankingsTests) - no longer the captured retail leaderboard. A guild appears on a tab only once it has
		// renown in that category, so a fresh server shows an empty board rather than fake guilds.
		private async Task SendGuildList(INetworkClient client, ConanPacket packet)
		{
			var realmId = client.Session.Character?.RealmId ?? 0;
			var guilds = await Database.GetGuilds(realmId);

			var rankings = GuildRankings.FromGuilds(
				guilds.Select(g => new GuildRankings.Row(g.Id, g.Name ?? "", g.RenownValor, g.RenownGlory, g.RenownArtistry, (uint)(g.Members?.Count ?? 0), g.Level)));

			await new PacketStream().WriteHeader(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildRankings.Opcode).WriteArray(rankings.Encode()).SendAsync(client);
		}

		// 0x205C DispatchGuildSearchResult: the guild-finder browse list (client requests it with 0x2043
		// SearchForGuilds). Built from the realm's guilds via GuildSearchResult (byte-exact codec,
		// GuildSearchResultTests). We ignore the 0x2043 level/name filter and return every guild in the realm; the
		// leader is the highest-ranked member.
		private async Task SendGuildSearchResult(INetworkClient client, ConanPacket packet)
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

			await new PacketStream().WriteHeader(GuildService.ReplySender(packet.Receiver), packet.Sender, GuildSearchResult.Opcode).WriteArray(result.Encode()).SendAsync(client);
		}

		// 0x2010 DispatchCharacterSearchResult: the character/"who" search result (answer to 0x200A CharacterSearch).
		// Body is a 0x3F1-framed list of player records; we return an empty list (no character-search index yet).
		private static Task SendCharacterSearchResult(INetworkClient client, ConanPacket packet)
		{
			return new PacketStream()
				.WriteHeader(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchCharacterSearchResult)
				.WriteUInt32(EmptyGuildList) // empty 0x3F1-framed record list
				.SendAsync(client);
		}

		// 0x2039 DispatchGuildRenownHistoryUpdate: the renown-over-time graph in the guild window (answer to 0x2040
		// RequestRenownHistory). The captured body is a fixed 112-byte block of renown/pvp time buckets; we track no
		// renown history, so we emit it zeroed (a flat graph) - the honest empty form for a guild with no history.
		private static Task SendGuildRenownHistory(INetworkClient client, ConanPacket packet)
		{
			return new PacketStream()
				.WriteHeader(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchGuildRenownHistoryUpdate)
				.WriteArray(new byte[112])
				.SendAsync(client);
		}

		// 0x20A5 DispatchMassivePvpInfoRequestReturn: the Massive-PvP minigame guild leaderboard (the client polls it
		// with 0x2077, and it rides alongside the 0x203F on a member-list request). Body decoded from the hoxxling
		// login capture: [u32 lastUpdateUnix][u32 (guildCount+1)*0x3F1][ guild records ], each record
		// [u32 rank][u32 rank][u32 a][u32 b][u32 c][u32 scoreA][u32 scoreB][u32 tsUnix][u32 guildId][s16 name][u32 0]
		// (the 0x3F1 count-framing that CoreGuild's list readers assert - see GuildService). We run no Massive-PvP
		// minigame, so we emit the empty list ([timestamp 0][(0+1)*0x3F1]) rather than replaying the captured retail
		// leaderboard; the client shows an empty board. Build the real records here if/when a minigame subsystem exists.
		private static Task SendMassivePvpInfo(INetworkClient client, ConanPacket packet)
		{
			return new PacketStream()
				.WriteHeader(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchMassivePvpInfoRequestReturn, [0x01])
				.WriteUInt32(0) // last-update timestamp (cosmetic; 0 = never updated)
				.WriteUInt32(EmptyGuildList) // empty 0x3F1-framed guild list
				.SendAsync(client);
		}

		// 0x2092 DispatchPvPMatchMakingInfoRequestReturn: PvP matchmaking info (answer to 0x2054
		// PvPMatchMakingRequestInfo). Retail's "not queued" body is [0x3F1][u32 0] (byte-exact from the login capture);
		// the header carries the same 0x01 extra byte as the capture.
		private static Task SendPvpMatchMakingInfo(INetworkClient client, ConanPacket packet)
		{
			return new PacketStream()
				.WriteHeader(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchPvPMatchMakingInfoRequestReturn, [0x01])
				.WriteUInt32(EmptyGuildList) // empty 0x3F1-framed record list
				.WriteUInt32(0) // trailer
				.SendAsync(client);
		}

		// 0x200F DispatchTeamSearchResult: the LFG team-search result (answer to 0x200B SearchForTeam). Body is a
		// 0x3F1-framed list + a trailing byte; retail's own "no results" body is [0x3F1][0x00] (byte-exact from the
		// login capture, where Hoxxling's search returned nothing).
		private static Task SendTeamSearchResult(INetworkClient client, ConanPacket packet)
		{
			return new PacketStream()
				.WriteHeader(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchTeamSearchResult)
				.WriteUInt32(EmptyGuildList) // empty 0x3F1-framed record list
				.WriteByte(0x00) // list trailer
				.SendAsync(client);
		}
	}
}
