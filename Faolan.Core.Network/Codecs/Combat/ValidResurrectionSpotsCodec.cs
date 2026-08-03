using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// GameCharInterface.DispatchValidResurrectionSpots (0x2024) - a reference position + the resurrection spots a
	// player may revive at. Retail sends it for BOTH of its uses (see IWorldOutbound.SendResurrectionSpots) - on death
	// with the death position, and on DISCOVERY with the live position, where re-sending the list with a new spot in it
	// IS the notification (#372).
	//
	//   [refPos X,Y,Z][u32 (count+1) * 0x3F1] then count x 41-byte records:
	//     [spotPos X,Y,Z][u32 spotType][u32 mapId][u32 0][u32 0][u32 0][u32 spotId][u32 restorationLevel][u8 0x00]
	//
	// Decoded from all 141 s2c 0x2024 frames in the captures (1/2/3/7 spots), every one of which parses under this
	// layout with nothing left over. Three fields that were carried as "undecoded constants" are real:
	//
	//   - The 0x7E2 "header" is the LIST COUNT: (count+1) * 0x3F1 - the protocol-wide framing (ListFrame). The four
	//     observed values are exactly 2/3/4/8 * 0x3F1 for the 1/2/3/7-spot frames.
	//   - The trailing 0x00 is PER-RECORD (stride 41), not one byte at the end of the body.
	//   - The 0x0A tail is RESTORATIONLEVEL, the percent of max the point revives you at (#375). Observed 0x0A/0x32/
	//     0x64 = 10/50/100, and for all 21 spot ids in the captures the wire value equals the RDB's RestorationLevel
	//     for that point - along with its map and position - to the byte.
	//
	// All three were invisible while we only ever sent ONE spot (the death case): at count 1 the hardcoded 0x7E2 IS
	// the correct prefix and a single end-of-body 0x00 lands exactly where the per-record one goes. They break at 2+.
	public static class ValidResurrectionSpotsCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchValidResurrectionSpots;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(ValidResurrectionSpots value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static ValidResurrectionSpots Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ValidResurrectionSpotsCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static ValidResurrectionSpots Read(ref SpanReader r)
		{
			var reference = r.ReadVector3Be();
			var spots = new ResurrectionSpot[r.ReadListCount()];
			for (var i = 0; i < spots.Length; i++)
			{
				var position = r.ReadVector3Be();
				var spotType = r.ReadU32Be();
				var mapId = r.ReadU32Be();
				var padA = r.ReadU32Be();
				var padB = r.ReadU32Be();
				var padC = r.ReadU32Be();
				var spotId = r.ReadU32Be();
				var restoration = r.ReadU32Be();
				spots[i] = new ResurrectionSpot(position, spotType, mapId, spotId, restoration, padA, padB, padC, r.ReadU8());
			}

			return new ValidResurrectionSpots(reference, spots);
		}

		internal static void Write(FaolanStream s, ValidResurrectionSpots v)
		{
			s.WriteVector3(v.ReferencePosition);
			s.WriteUInt32(ListFrame.ForCount(v.Spots.Count));
			foreach (var spot in v.Spots)
			{
				s.WriteVector3(spot.Position);
				s.WriteUInt32(spot.SpotType);
				s.WriteUInt32(spot.MapId);
				s.WriteUInt32(spot.PadA).WriteUInt32(spot.PadB).WriteUInt32(spot.PadC);
				s.WriteUInt32(spot.SpotId); // the spawnPointId the client echoes back in RespawnMe
				s.WriteUInt32(spot.RestorationLevel); // percent of max this point revives you at (#375)
				s.WriteByte(spot.Terminator);
			}
		}
	}

	// The DispatchValidResurrectionSpots body.
	public sealed record ValidResurrectionSpots(Vector3 ReferencePosition, IReadOnlyList<ResurrectionSpot> Spots);

	// One 41-byte spot record.
	public sealed record ResurrectionSpot(
		Vector3 Position,
		uint SpotType,
		uint MapId,
		uint SpotId,
		uint RestorationLevel,
		uint PadA = 0,
		uint PadB = 0,
		uint PadC = 0,
		byte Terminator = 0x00);

	// The two send forms, as VALUE factories - they differ only in the per-record type word and where the spots come
	// from, so they cannot be told apart by a `Build` signature.
	public static class ValidResurrectionSpotLists
	{
		public const uint SpotTypeCreateFlow = 0x0000C79E;

		// The per-record type word. UNDECODED, and per-PACKET rather than per-spot: point 790836 appears under both
		// 0xC79C and 0xC7A0 in different frames, so it cannot be a property of the point and is not stored on it.
		// Kept verbatim per call site. 0xC79C is also observed (Hakotep_hox_login_gameplay0 f=5274) in a context we do
		// not reproduce; what selects the third value is unknown.
		public const uint SpotTypeLogin = 0x0000C7A0;

		// The create-flow variant: one spot at the fresh character's spawn point, spawnPointId 0 (no anchor chosen
		// yet), restoration 100 (a new character revives at full). Byte-identical in SHAPE to the login body - only
		// the type word differs - which the 41-byte record proves: the create capture's tail is a u32 0x64 (=
		// restoration 100), NOT the u16 0x64 this used to write. That mis-read left every create-flow packet we sent
		// 2 bytes short of retail's 57.
		public static ValidResurrectionSpots CreateFlow(Vector3 spawnPosition)
		{
			return new ValidResurrectionSpots(spawnPosition, [new ResurrectionSpot(spawnPosition, SpotTypeCreateFlow, Map.TortageBeachId, 0, 100)]);
		}

		public static ValidResurrectionSpots Known(Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots)
		{
			// Retail orders the list NEAREST-FIRST from the reference position; the client renders it in wire order, so
			// this is what puts the closest spot at the top of the release screen. Proven rather than guessed: the
			// three 7-spot frames carry the SAME seven points in three DIFFERENT orders, each ascending by distance
			// from that frame's own reference position. Ascending distance holds in 23/23 multi-spot frames, whereas
			// ordering by id holds in only 16/23.
			var ordered = spots
				.OrderBy(spot => Vector3.DistanceSquared(spot.Position, referencePosition))
				.Select(spot => new ResurrectionSpot(spot.Position, SpotTypeLogin, spot.MapId, spot.Id, (uint)spot.RestorationLevel))
				.ToList();

			return new ValidResurrectionSpots(referencePosition, ordered);
		}
	}
}
