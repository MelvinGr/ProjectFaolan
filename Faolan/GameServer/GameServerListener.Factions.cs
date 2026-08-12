using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Faction;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;

namespace Faolan.GameServer
{
	// The wire side of the Khitai reputation system (Gitea #310) - the one outbound op `FactionService` needs, on the
	// same GameCharInterface conversation every other per-character dispatch rides.
	//
	// ⚠️ ZERO captures back these two messages, in either direction: the corpus carries no Khitai play at all. They
	// are not guessed from that silence - the client's own method table names their parameters and the bodies are the
	// parameters as bare big-endian values, which is #132's rule for every scalar method on these interfaces. See the
	// codecs and docs/subsystems/factions.md.
	public partial class GameServerListener
	{
		// ⭐ BOTH messages, always together. The client keeps the two halves of a standing in different places - the
		// bar off the POINTS, the rank title off the RANK - and a server that moved only one would leave the other
		// showing whatever it was last told. The rank is derived (`FactionLadder.RankFor`), never stored, so the two
		// cannot disagree about the same standing.
		//
		// Points first, then rank: a rank is a threshold ON the points, so the client has the number the title is
		// about before it is told the title. Nothing captured says the order matters; this is the one that cannot
		// read as a title arriving for a standing the client has not seen.
		private async Task SendFactionStanding(INetworkClient client, uint factionId, int points, uint rank)
		{
			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchApplyFactionPoints, ApplyFactionPointsCodec.Build(factionId, points)));

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchApplyFactionRank, ApplyFactionRankCodec.Build(factionId, rank)));
		}
	}
}
