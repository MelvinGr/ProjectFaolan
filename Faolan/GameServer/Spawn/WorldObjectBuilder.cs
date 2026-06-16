using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Spawn
{
	// Bridges a stored WorldObject row to a wire body (66AEDD50/F096251E/33A56FB0/6BD56736/1FD6412B), addressed to a
	// live session handle. Sibling of NpcSpawnBuilder, but simpler: a world object has no CharacterIIR to splice, so
	// only the transform is rebuilt (from the row's columns) - the object message + raw tail are carried over from
	// the row's captured Data. The sub-opcode is preserved from the template, so one builder handles every object
	// class. Byte-identical to the captured body when the row's columns match the capture (WorldObjectBuilderTests).
	public static class WorldObjectBuilder
	{
		public static bool TryBuild(WorldObject row, uint handle, out byte[] body)
		{
			return TryBuild(row, handle, new Vector3(row?.PositionX ?? 0, row?.PositionY ?? 0, row?.PositionZ ?? 0), new Quaternion(row?.RotationX ?? 0, row?.RotationY ?? 0, row?.RotationZ ?? 0, row?.RotationW ?? 1), out body);
		}

		// As TryBuild, but rewrites the transform's playfield-instance varint to `playfieldInstance` (the live
		// per-session world token). The row's stored Data carries whatever session it was captured in, so a
		// from-scratch placement (no live body to inherit from, e.g. the create-editor scene props) must thread the
		// current session token in. Session tokens are 4-byte LEB128 varints, so the rewrite is splice-free. Returns
		// false if the build fails, the playfield can't be located, or the token isn't a 4-byte varint.
		public static bool TryBuild(WorldObject row, uint handle, uint playfieldInstance, out byte[] body)
		{
			return TryBuild(row, handle, out body) && TryRewritePlayfieldInstance(body, playfieldInstance);
		}

		// As TryBuild, but places the object at an EXPLICIT transform rather than the row's captured one - used to
		// drop a templated object (e.g. a loot bag) at a runtime location like an NPC's death spot.
		public static bool TryBuild(WorldObject row, uint handle, Vector3 position, Quaternion rotation, out byte[] body)
		{
			body = null;
			var template = ExtractBody(row?.Data);
			if (template == null || template.Length < SpawnPacketBuilder.RotWOffset + 4)
				return false;

			body = SpawnPacketBuilder.BuildSpawnBody(template, handle, position, rotation);

			// For an object whose identity has been lifted into columns (#187), CONSTRUCT the ProtoSimpleDynelIIR
			// message from those columns and splice it over the carried-from-Data identity. EVERY world-object class
			// (SimpleDynel/ClimbingDynel/ZoneLabel/Destructible/LootBag) carries the same identity message as its
			// first blob, so this is gated on the lifted columns (DisplayName != null, set by the self-validating
			// importer), not on the class. The body is the IIR carrier [u32 len][u32 subOpcode][record], so the codec
			// operates on `body[8..]`; the per-class tail + transform stay template-carried (interim). Byte-identical.
			if (body.Length > 8 && SimpleDynelMessageCodec.TryReplaceIdentityFromColumns(body.AsSpan(8), row, out var newRecord))
			{
				var combined = new byte[8 + newRecord.Length];
				body.AsSpan(0, 8).CopyTo(combined);
				newRecord.CopyTo(combined.AsSpan(8));
				BinaryPrimitives.WriteUInt32BigEndian(combined.AsSpan(0, 4), (uint)(4 + newRecord.Length)); // carrier len = [subOpcode] + record
				body = combined;
			}

			return true;
		}

		// Builds a body by splicing the DB row's object IDENTITY (the length-delimited message + the raw tail,
		// everything after the transform) onto the LIVE placement (`liveBody`'s header + transform + playfield).
		// The placement (handle / position / playfield = the per-session world id) stays live; only the object
		// definition comes from the table. For the same object captured in another session this is byte-identical
		// to `liveBody` (the identity is session-independent - only the handle + playfield vary), so it drives the
		// object from the DB with zero render risk. Returns false if either body's transform can't be parsed.
		public static bool TryBuildOntoLivePlacement(WorldObject row, ReadOnlySpan<byte> liveBody, out byte[] body)
		{
			body = null;
			if (!TryMessageOffset(liveBody, out var liveOffset))
				return false;

			// Lifted row (#187): CONSTRUCT the identity from columns + append the stored geometry tail - no Data
			// slice read at all. Byte-identical to the verbatim splice (the columns reproduce the captured identity,
			// the tail is stored verbatim), so the world-entry object is now fully DB-driven.
			if (row?.DisplayName != null && row.KdTreeTail != null)
			{
				var identity = SimpleDynelMessageCodec.BuildMessage(SimpleDynelMessageCodec.ToMessage(row));
				body = new byte[liveOffset + identity.Length + row.KdTreeTail.Length];
				liveBody[..liveOffset].CopyTo(body);
				identity.CopyTo(body.AsSpan(liveOffset));
				row.KdTreeTail.CopyTo(body.AsSpan(liveOffset + identity.Length));
				return true;
			}

			// Fallback (un-lifted row): splice the row's identity + tail verbatim from its captured Data.
			var template = ExtractBody(row?.Data);
			if (template == null || !TryMessageOffset(template, out var rowOffset))
				return false;

			body = new byte[liveOffset + (template.Length - rowOffset)];
			liveBody[..liveOffset].CopyTo(body);
			template.AsSpan(rowOffset).CopyTo(body.AsSpan(liveOffset));
			return true;
		}

		// The captured `Data` is the full Conan packet ([u32 len][u32 crc][u32 headerLen][header][body]); the
		// object body starts after the 12-byte prefix + the header. Returns null if the framing is malformed.
		// Public so the importer can lift the dynel identity columns from the same body the builder consumes.
		public static byte[] ExtractBody(byte[] data)
		{
			if (data == null || data.Length < 12)
				return null;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= data.Length
				? data[bodyStart..]
				: null;
		}

		// Rewrites the transform's playfield-instance varint (the `18 <varint>` at the end of the 0x2C transform
		// group) to `value`, in place. `body` is the carrier payload [u32 len][u32 subOpcode][record]; the transform
		// group starts inside the record. Requires both the existing and the new varint to be 4 bytes (session
		// tokens are in [2^21, 2^28)), so length - and therefore every surrounding length field - is preserved.
		private static bool TryRewritePlayfieldInstance(byte[] body, uint value)
		{
			if (value is < (1u << 21) or >= (1u << 28))
				return false;

			var record = body.AsSpan(8); // skip [u32 len][u32 subOpcode]
			if (!TryPlayfieldVarintStart(record, out var start) || start + 4 > record.Length)
				return false;
			for (var k = 0; k < 4; k++) // existing token must also be a 4-byte varint
				if (((record[start + k] & 0x80) != 0) != (k < 3))
					return false;

			record[start + 0] = (byte)(0x80 | (value & 0x7F));
			record[start + 1] = (byte)(0x80 | ((value >> 7) & 0x7F));
			record[start + 2] = (byte)(0x80 | ((value >> 14) & 0x7F));
			record[start + 3] = (byte)((value >> 21) & 0x7F);
			return true;
		}

		// Offset (within a dynel record body) of the playfield-instance varint that follows the 0x18 tag at the end
		// of the 0x2C transform group: skip the 12-byte header, the 2C group tag, the 0A vec3, the 12 quat, the
		// optional 25 scale, then the 0x18 tag.
		private static bool TryPlayfieldVarintStart(ReadOnlySpan<byte> b, out int start)
		{
			start = 0;
			var i = 12;
			if (b.Length <= i || b[i] != 0x2C)
				return false;
			i++;
			if (i + 1 >= b.Length || b[i] != 0x0A)
				return false;
			i += 2 + b[i + 1]; // 0A vec3
			if (i + 1 >= b.Length || b[i] != 0x12)
				return false;
			i += 2 + b[i + 1]; // 12 quat
			if (i < b.Length && b[i] == 0x25)
				i += 5; // optional 25 scale
			if (i >= b.Length || b[i] != 0x18)
				return false;
			start = i + 1;
			return true;
		}

		// Offset where the object message's u32 length prefix begins: just after the 0x2C transform group
		// (0A vec3, 12 quat, optional 25 scale, 18 playfield-varint). No pre-IIR block, unlike entity spawns.
		private static bool TryMessageOffset(ReadOnlySpan<byte> b, out int offset)
		{
			offset = 0;
			var i = 20;
			if (b.Length <= i || b[i] != 0x2C)
				return false;

			i++;
			if (i + 1 >= b.Length || b[i] != 0x0A)
				return false;

			i += 2 + b[i + 1]; // 0A vec3
			if (i + 1 >= b.Length || b[i] != 0x12)
				return false;

			i += 2 + b[i + 1]; // 12 quat
			if (i < b.Length && b[i] == 0x25)
				i += 5; // optional 25 scale
			if (i >= b.Length || b[i] != 0x18)
				return false;

			i++;
			while (i < b.Length && (b[i] & 0x80) != 0) // playfield varint
				i++;
			i++;
			offset = i;
			return i + 4 <= b.Length;
		}
	}
}
