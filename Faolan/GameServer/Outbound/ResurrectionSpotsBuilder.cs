using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// GameCharInterface.DispatchValidResurrectionSpots (s2c 0x2024) - the death/release screen. On death the server
	// sends the player's death position + the valid resurrection spots; the client shows the release UI and replies
	// with RespawnMe(spawnPointId) (c2s 0x2024) carrying the chosen spot's id, which we look up to respawn there.
	//
	// Decoded from account-faolan-...-resurect (idx616/idx4050, two deaths, bodies identical bar the death pos);
	// golden-tested in DispatchValidResurrectionSpotsTests. See agent memory protocol_death_resurrect.
	//
	// Wire body (57 bytes for one spot, all big-endian):
	//   [deathPos X,Y,Z : 3 floats][u32 0x7E2 header][ spot ]* [u8 0x00 trailing]
	// Per spot (41 bytes):
	//   [spotPos X,Y,Z : 3 floats][u32 0xC7A0 const][u32 mapId][u32 0][u32 0][u32 0][u32 spawnPointId][u32 0x0A const]
	// 0x7E2/0xC7A0/0x0A + the trailing 0x00 are capture-derived constants (undecoded), kept verbatim. The captures
	// only ever carried ONE spot, so the multi-spot list framing is not pinned - we send one (the closest known point).
	public partial class GameServerListener
	{
		private const uint ResSpotConstA = 0x0000C7A0;
		private const uint ResSpotConstB = 0x0000000A;

		// Capture-derived constants whose semantics aren't decoded yet (same in both captured deaths).
		private const uint ResSpotsHeader = 0x000007E2;

		// Sender is GameCharAgent at instance 0x59 (NB: NOT 0x58 like the add/remove-item LoginWorld201CSender).
		private static readonly byte[] ResurrectionSpotsSender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x59);

		// Pushes the death/release screen: the player's death position + the resurrection spots they can pick.
		private Task SendValidResurrectionSpots(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			var ps = new PacketStream();
			ps.WriteHeader(ResurrectionSpotsSender, LoginWorld201CReceiver(client), GameCharInterfaceDispatchOpcodes.DispatchValidResurrectionSpots);
			WriteValidResurrectionSpotsBody(ps, deathPosition, spots);
			return ps.SendAsync(client);
		}

		internal static void WriteValidResurrectionSpotsBody(FaolanStream s, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			s.WriteVector3BigEndian(deathPosition);
			s.WriteUInt32(ResSpotsHeader);

			foreach (var spot in spots)
			{
				s.WriteFloatBigEndian(spot.PositionX).WriteFloatBigEndian(spot.PositionY).WriteFloatBigEndian(spot.PositionZ);
				s.WriteUInt32(ResSpotConstA);
				s.WriteUInt32(spot.MapId);
				s.WriteUInt32(0).WriteUInt32(0).WriteUInt32(0);
				s.WriteUInt32(spot.Id); // the spawnPointId the client echoes back in RespawnMe
				s.WriteUInt32(ResSpotConstB);
			}

			s.WriteByte(0x00);
		}
	}
}
