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
		// 0x2038 DispatchGuildRankingsUpdate: the global guild-renown leaderboard (the "Guild Renown Rankings" window,
		// tabs Total/Valor/Glory/Artistry). Built from the realm's guilds via GuildRankings (byte-exact codec,
		// GuildRankingsTests) - no longer the captured retail leaderboard. A guild appears on a tab only once it has
		// renown in that category, so a fresh server shows an empty board rather than fake guilds.
		private async Task SendGuildList(INetworkClient client, ConanPacket packet)
		{
			var realmId = client.Session.Character?.RealmId ?? 0;
			var guilds = await Database.GetGuilds(realmId);

			var rankings = GuildRankings.FromGuilds(guilds.Select(g => new GuildRankings.Row(
				g.Id, g.Name ?? "", g.RenownValor, g.RenownGlory, g.RenownArtistry, (uint)(g.Members?.Count ?? 0), g.Level)));

			await new PacketStream()
				.WriteHeader(GuildService.ReplySender(packet.Receiver), packet.Sender, CSPlayerInterfaceOpcodes.DispatchGuildRankingsUpdate)
				.WriteArray(rankings.Encode())
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
				.WriteUInt32(GuildService.GuildListMultiplier) // (0 guilds + 1) * 0x3F1 - the empty 0x3F1-framed list
				.SendAsync(client);
		}
	}
}
