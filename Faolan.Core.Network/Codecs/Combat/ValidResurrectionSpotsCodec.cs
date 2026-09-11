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
	/// <summary>
	///     GameCharInterface.DispatchValidResurrectionSpots - a reference position and the resurrection spots a player may
	///     revive at. Retail sends it for both of its uses (see IWorldOutbound.SendResurrectionSpots): on death with the
	///     death position, and on discovery with the live position, where re-sending the list with a new spot in it is the
	///     notification.
	/// </summary>
	/// <remarks>
	///     The body is a reference position, a <see cref="ListFrame" /> count, then that many 41-byte records:
	///     <code>
	///     [refPos X,Y,Z][ListFrame(count)]
	///     [spotPos X,Y,Z][u32 spotType][u32 mapId][u32 0][u32 0][u32 0][u32 spotId][u32 restorationLevel][u8 0x00]
	///     </code>
	///     Decoded from all 141 captured s2c frames (1, 2, 3 and 7 spots), every one of which parses under this layout with
	///     nothing left over. Three fields are easy to mistake for constants because a one-spot body hides them:
	///     <list type="bullet">
	///         <item>The word after the reference position is the list count, not a header.</item>
	///         <item>The trailing 0x00 is per-record, at stride 41, not one byte at the end of the body.</item>
	///         <item>
	///             The last word of a record is the restoration level, the percent of max the point revives you at.
	///             Observed at 10, 50 and 100, and for all 21 spot ids in the captures the wire value equals the RDB's
	///             RestorationLevel for that point - along with its map and position - to the byte.
	///         </item>
	///     </list>
	/// </remarks>
	public static class ValidResurrectionSpotsCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchValidResurrectionSpots;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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
				s.WriteUInt32(spot.RestorationLevel); // percent of max this point revives you at
				s.WriteByte(spot.Terminator);
			}
		}
	}

	/// <summary>The DispatchValidResurrectionSpots body.</summary>
	public sealed record ValidResurrectionSpots(Vector3 ReferencePosition, IReadOnlyList<ResurrectionSpot> Spots);

	/// <summary>One 41-byte spot record.</summary>
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

	/// <summary>
	///     The two send forms, as value factories: they differ only in the per-record type word and where the spots come
	///     from, so a Build signature could not tell them apart.
	/// </summary>
	public static class ValidResurrectionSpotLists
	{
		public const uint SpotTypeCreateFlow = 0x0000C79E;

		/// <summary>
		///     The per-record type word. Undecoded, and per-packet rather than per-spot: point 790836 appears under both
		///     0xC79C and 0xC7A0 in different frames, so it cannot be a property of the point and is not stored on one. Each
		///     call site states its own. 0xC79C is also observed (Hakotep_hox_login_gameplay0 f=5274) in a context we do not
		///     reproduce; what selects it is unknown.
		/// </summary>
		public const uint SpotTypeLogin = 0x0000C7A0;

		/// <summary>
		///     The create-flow variant: one spot at the fresh character's spawn point, spawnPointId 0 because no anchor has
		///     been chosen yet, and restoration 100 because a new character revives at full. It is the same shape as the
		///     login body and only the type word differs; the create capture's record tail is a u32 restoration of 100, which
		///     is what makes the record 41 bytes and the packet 57.
		/// </summary>
		public static ValidResurrectionSpots CreateFlow(Vector3 spawnPosition)
		{
			return new ValidResurrectionSpots(spawnPosition, [new ResurrectionSpot(spawnPosition, SpotTypeCreateFlow, Map.TortageBeachId, 0, 100)]);
		}

		public static ValidResurrectionSpots Known(Vector3 referencePosition, IReadOnlyList<RespawnPoint> spots)
		{
			// Retail orders the list nearest-first from the reference position, and the client renders it in wire order,
			// so this is what puts the closest spot at the top of the release screen. The three 7-spot frames carry the
			// same seven points in three different orders, each ascending by distance from that frame's own reference
			// position; ascending distance holds in all 23 multi-spot frames, while ordering by id holds in only 16.
			var ordered = spots
				.OrderBy(spot => Vector3.DistanceSquared(spot.Position, referencePosition))
				.Select(spot => new ResurrectionSpot(spot.Position, SpotTypeLogin, spot.MapId, spot.Id, (uint)spot.RestorationLevel))
				.ToList();

			return new ValidResurrectionSpots(referencePosition, ordered);
		}
	}
}
