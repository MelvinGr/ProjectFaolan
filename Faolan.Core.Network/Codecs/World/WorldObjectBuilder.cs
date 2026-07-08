using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;

namespace Faolan.Core.Network.Codecs
{
	// Bridges a stored WorldObject row to a wire body (66AEDD50/F096251E/33A56FB0/6BD56736/1FD6412B), addressed to a
	// live session handle. Sibling of NpcSpawnBuilder, but simpler: a world object has no CharacterIIR to splice, so
	// only the transform is rebuilt (from the row's columns) - the object message + raw tail are carried over from
	// the row's captured Data. The sub-opcode is preserved from the template, so one builder handles every object
	// class. Byte-identical to the captured body when the row's columns match the capture (WorldObjectBuilderTests).
	public static class WorldObjectBuilder
	{
		// Builds a world-object body FROM SCRATCH (#249) - no captured `Data`: the header (subOpcode + typeTag +
		// handle, fieldA=0) + the transform (pos/rot + the live playfield token as the 0x18 4-byte varint) + the
		// identity message generated from the row's columns (SimpleDynelMessageCodec) + the row's KdTreeTail (the
		// static geometry). Byte-identical to a captured body when fed that object's columns + KdTreeTail + transform
		// (WorldObjectBuilderTests). This is how a loot bag is spawned with NO captured world-object row in the world
		// (the loot system's bag definition is a baked constant - LootBagTemplate). Returns false if the row has no
		// lifted columns (DisplayName) or no KdTreeTail.
		public static bool BuildFromScratch(WorldObject row, uint handle, ProtoVec3 position, ProtoQuat rotation, uint typeTag, uint playfieldToken, out byte[] body)
		{
			body = null;
			if (row?.DisplayName == null || row.KdTreeTail == null)
				return false;

			var head = new FaolanStream()
				.WriteUInt32(0) // body length placeholder @0 (set below)
				.WriteUInt32(row.SubOpcode)
				.WriteUInt32(typeTag)
				.WriteUInt32(handle)
				.WriteUInt32(0) // fieldA
				.WriteByte(0x2C)
				.WriteByte(0x0A)
				.WriteArrayByteLength(position.ToProtobuf())
				.WriteByte(0x12)
				.WriteArrayByteLength(rotation.ToProtobuf())
				.WriteByte(0x18)
				.WriteArray(EncodePlayfieldVarint(playfieldToken))
				.ToArray();

			var identity = SimpleDynelMessageCodec.BuildMessage(SimpleDynelMessageCodec.ToMessage(row)); // [u32 len][record]

			body = new byte[head.Length + identity.Length + row.KdTreeTail.Length];
			head.CopyTo(body, 0);
			identity.CopyTo(body, head.Length);
			row.KdTreeTail.CopyTo(body, head.Length + identity.Length);
			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)(body.Length - 4));
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

		// Identity discriminator for deduping/matching world objects: the SimpleDynel `display_name` (a stable
		// localized id), or "" for object classes that carry no such id. Two distinct props can sit at the EXACT
		// same coordinates (e.g. the map-4000 editor scene has localized 347659 and 3072626 at one spot, #222), so
		// position alone is not a unique key - the dedup and the burst/world-entry matching both key by (position,
		// identity). Reads from a carrier body ([u32 len][u32 subOpcode][record]); the record's identity message is
		// the same ProtoSimpleDynelIIR the lift codec parses. "" when the class has no display_name or the body is
		// malformed (such objects don't collide, so position alone keeps disambiguating them).
		public static string ReadIdentity(ReadOnlySpan<byte> carrierBody)
		{
			if (carrierBody.Length <= 8)
				return string.Empty;

			var subOpcode = BinaryPrimitives.ReadUInt32BigEndian(carrierBody.Slice(4, 4));
			return subOpcode == SimpleDynelMessageCodec.SubOpcode && SimpleDynelMessageCodec.TryParse(carrierBody.Slice(8), out var message, out _, out _)
				? message.DisplayName ?? string.Empty
				: string.Empty;
		}

		// As ReadIdentity(body), reading the identity from a stored row's OWN captured Data. Used to key the table
		// rows the same way the burst bodies are keyed, so the two sides always agree regardless of lift status.
		public static string ReadIdentity(WorldObject row)
		{
			var body = ExtractBody(row?.Data);
			return body == null
				? string.Empty
				: ReadIdentity(body);
		}

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
			return TryBuild(row, handle, out body) && SpawnPlayfieldRewriter.TryRewrite(body, playfieldInstance);
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
			if (row is { DisplayName: not null, KdTreeTail: not null })
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

		// The playfield token in the transform is a 4-byte LEB128 varint (the fixed width SpawnPlayfieldRewriter
		// assumes); tokens are < 2^28 so 4 bytes always suffice.
		private static byte[] EncodePlayfieldVarint(uint token)
		{
			return
			[
				(byte)((token & 0x7F) | 0x80),
				(byte)(((token >> 7) & 0x7F) | 0x80),
				(byte)(((token >> 14) & 0x7F) | 0x80),
				(byte)((token >> 21) & 0x7F)
			];
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
