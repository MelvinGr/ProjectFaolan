using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchAddWayPoint(i_cID, i_nPlayfieldID, i_cPos, i_vRadius, i_nLabel, i_nToolTip) - GameCharInterface 0x7A.
	// The player's MAP MARKER: the client draws a pin the message names, on the playfield the message names (#868).
	//
	//   [ID32 i_cID = [u32 class][u32 instance]][u32 i_nPlayfieldID][f32 x][f32 y][f32 z][f32 i_vRadius]
	//   [u32 i_nLabel][u32 i_nToolTip]
	//
	// Nine u32s, 36 bytes, which is exactly what the corpus carries on all six captured frames. Field names are the
	// client's own signature; the floats are big-endian like every other raw-body float.
	//
	// ⭐ **Every captured waypoint is a TOMBSTONE.** `i_nLabel` is `0x0F5A0B53` and `i_nToolTip` is `0x013AF090` on
	// all six, and those resolve in the LDB (category 100) to *"Tombstone"* and *"Locate your Tombstone to reduce
	// death penalty!"*. `i_cID`'s class half is `0xC87B` = `EntityClass.TombStone` on all six too - so the marker
	// points at the dead player's own corpse entity, and the pin IS the death-penalty retrieval marker.
	// `i_vRadius` is 0.0 on every captured frame.
	//
	// ⚠️ Six frames cannot settle a grammar by variance alone; what settles this one is the signature's six fields
	// landing exactly on 36 bytes, and the twin below re-using the first twelve.
	public static class WayPointCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchAddWayPoint;

		// The one label/tooltip pair the corpus carries, kept named rather than as two hex literals at a call site.
		public const uint TombstoneLabel = 0x0F5A0B53;
		public const uint TombstoneToolTip = 0x013AF090;

		public static byte[] Build(WayPoint value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		// The captured shape: a tombstone pin on an entity, at a point, with no radius.
		public static byte[] Build(uint tombstoneInstance, uint playfieldId, float x, float y, float z)
		{
			return Build(new WayPoint(new EntityId(EntityClass.TombStone, tombstoneInstance), playfieldId, x, y, z, 0f,
				TombstoneLabel, TombstoneToolTip));
		}

		public static WayPoint Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(WayPointCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static WayPoint Read(ref SpanReader r)
		{
			var id = EntityIdCodec.Read(ref r);
			var playfieldId = r.ReadU32Be();
			var x = r.ReadF32Be();
			var y = r.ReadF32Be();
			var z = r.ReadF32Be();
			var radius = r.ReadF32Be();
			var label = r.ReadU32Be();

			return new WayPoint(id, playfieldId, x, y, z, radius, label, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, WayPoint v)
		{
			EntityIdCodec.Write(s, v.Id);
			s.WriteUInt32(v.PlayfieldId).WriteFloat(v.X).WriteFloat(v.Y).WriteFloat(v.Z).WriteFloat(v.Radius)
				.WriteUInt32(v.Label).WriteUInt32(v.ToolTip);
		}
	}

	public sealed record WayPoint(EntityId Id, uint PlayfieldId, float X, float Y, float Z, float Radius, uint Label, uint ToolTip);
}
