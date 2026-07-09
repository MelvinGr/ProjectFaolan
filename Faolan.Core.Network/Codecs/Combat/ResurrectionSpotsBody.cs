using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs.Combat
{
	// The ValidResurrectionSpots body: [deathPos X,Y,Z][u32 header] then per spot
	//   [spotPos X,Y,Z][u32 0xC7A0][u32 mapId][u32 0][u32 0][u32 0][u32 spawnPointId][u32 0x0A] then a trailing 0x00.
	// 0x7E2/0xC7A0/0x0A + the trailing byte are capture-derived constants (undecoded), kept verbatim. Extracted from
	// the GameServerListener.ResurrectionSpots partial; the listener keeps the framing + send.
	public static class ResurrectionSpotsBody
	{
		private const uint ResSpotConstA = 0x0000C7A0;
		private const uint ResSpotConstB = 0x0000000A;
		private const uint ResSpotsHeader = 0x000007E2;

		// The CREATE-FLOW variant of the ValidResurrectionSpots body: the same shape as WriteValidResurrectionSpotsBody
		// (both positions = the fresh char's spawn point, spawnPointId 0 = no anchor chosen yet) but with the create
		// capture's own undecoded constants - ConstA 0xC79E (vs the login 0xC7A0) and a u16 0x64 tail (vs the login u32
		// 0x0A). Distinct constants, so it can't call the login builder without changing the create-flow wire bytes.
		public static void WriteCreateFlowValidResurrectionSpots(FaolanStream s, Vector3 spawnPosition)
		{
			s.WriteVector3(spawnPosition); // death position
			s.WriteUInt32(ResSpotsHeader);
			s.WriteVector3(spawnPosition); // spot position (= the spawn point)
			s.WriteUInt32(0x0000C79Eu); // create-flow ConstA (login uses 0xC7A0)
			s.WriteUInt32(Map.TortageBeachId); // spot map id (0xFAA = the beach)
			s.WriteUInt32(0).WriteUInt32(0).WriteUInt32(0);
			s.WriteUInt32(0); // spawnPointId - 0 for a fresh char
			s.WriteUInt16(0x0064); // create-flow ConstB (login uses u32 0x0A)
			s.WriteByte(0x00); // trailing
		}

		public static void WriteValidResurrectionSpotsBody(FaolanStream s, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			s.WriteVector3(deathPosition);
			s.WriteUInt32(ResSpotsHeader);

			foreach (var spot in spots)
			{
				s.WriteVector3(spot.Position);
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
