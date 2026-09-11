using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchAddWayPoint(i_cID, i_nPlayfieldID, i_cPos, i_vRadius, i_nLabel, i_nToolTip) - GameCharInterface 0x7A.
	// The player's map marker: the client draws a pin the message names, on the playfield the message names.
	//
	//   [ID32 i_cID = [u32 class][u32 instance]][u32 i_nPlayfieldID][f32 x][f32 y][f32 z][f32 i_vRadius]
	//   [u32 i_nLabel][u32 i_nToolTip]
	//
	// Nine u32s, 36 bytes, which is exactly what the corpus carries on all six captured frames. Field names are the
	// client's own signature; the floats are big-endian like every other raw-body float. Six frames cannot settle a
	// grammar by variance alone; what settles this one is the signature's six fields landing exactly on 36 bytes, the
	// twin below re-using the first twelve, and the client's own reader (`FUN_004c9860`) reading exactly these nine
	// words.
	//
	// Every captured waypoint is a tombstone: `i_nLabel` is `0x0F5A0B53` and `i_nToolTip` is `0x013AF090` on all six,
	// which resolve in the LDB (category 100) to "Tombstone" and "Locate your Tombstone to reduce death penalty!".
	// `i_cID`'s class half is `0xC87B` = `EntityClass.TombStone` on all six too, so the marker points at the dead
	// player's own corpse entity and the pin is the death-penalty retrieval marker. `i_vRadius` is 0.0 on every
	// captured frame.
	//
	// What the fields do, per the client's receive chain: `GameCharInterfaceDispatch_t::DispatchAddWayPoint`
	// (`FUN_004c9860`) -> vtable +0x1ec -> `FUN_0139c1f0` (GamecodeClient_t) -> `FUN_01398160` -> `FUN_013a18f0`
	// (`GUI\MapWaypointHandler.cpp`) -> `FUN_0139f520`, the add.
	//
	// - `i_nLabel` is the pin's title text and `i_nToolTip` its tooltip text. `FUN_013a18f0` passes each to
	//   `FUN_0060f190(100, id, 0)` - the LDB text resolver, category 100, the same call shape that resolves a respawn
	//   point's name out of category 270 - and hands the two resulting strings to the add.
	// - `i_cID` never reaches the add: `FUN_0139f520` is `RET 0x20`, exactly eight stack arguments, and all eight are
	//   accounted for at the call site (handler, `i_cPos`, `i_vRadius`, title, tooltip, const, record, const).
	//   `i_cID` is stored into a local in `FUN_013a18f0` and is not among them. It is the marker key, which is what
	//   `DispatchRemoveWayPoint` addresses the pin by.
	//
	// The icon comes from a waypoint type the client carries in that record struct, and the server does not send one:
	// `FUN_013a18f0` zeroes the record's three words. The client's own waypoint creators do set it - `FUN_013a2f80`
	// writes 7 for a resurrection point and 0x1C for a murder resurrection point - and the client has a named enum for
	// them (`ParseWPType`, `FUN_0139e6f0`: names include `destination`, `dungeon` and `custom`; its inverse
	// `FUN_0139e8d0` returns strings for 8, 9, 0x0D, 0x15 and 0x16 and defaults to `custom`), plus per-playfield
	// definitions in `Gui/Default/Waypoints/PF%u.xml` whose attributes are `tooltip_title` / `tooltip_text` /
	// `min_level` / `max_level` / `tooltip_image` / `normal_bitmap` / `inactive_bitmap` / `dir_arrow` /
	// `dir_arrow_inactive`.
	//
	// So every pin this message plants gets the same default look whatever `i_cID` or `i_nLabel` carry. What the
	// message can vary is the text, which is why `Build` takes the label and tooltip rather than hardcoding the
	// tombstone pair. [V1: FUN_013a18f0 / FUN_0139f520 in AgeOfConan.exe]
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

		/// <summary>The captured shape: a pin on an entity, at a point, with no radius.</summary>
		/// <param name="label">An LDB category-100 string id - the pin's title text. Defaults to the captured tombstone pair.</param>
		/// <param name="toolTip">An LDB category-100 string id - the pin's tooltip text.</param>
		public static byte[] Build(uint tombstoneInstance, uint playfieldId, float x, float y, float z,
			uint label = TombstoneLabel, uint toolTip = TombstoneToolTip)
		{
			return Build(new WayPoint(new EntityId(EntityClass.TombStone, tombstoneInstance), playfieldId, x, y, z, 0f,
				label, toolTip));
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
