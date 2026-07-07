using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network.Codecs
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

		public static void WriteValidResurrectionSpotsBody(FaolanStream s, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			s.WriteVector3(deathPosition);
			s.WriteUInt32(ResSpotsHeader);

			foreach (var spot in spots)
			{
				s.WriteFloat(spot.PositionX).WriteFloat(spot.PositionY).WriteFloat(spot.PositionZ);
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
