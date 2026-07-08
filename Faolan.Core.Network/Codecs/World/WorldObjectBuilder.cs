using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;

namespace Faolan.Core.Network.Codecs
{
	// Builds a stored WorldObject row into a wire body (66AEDD50/F096251E/33A56FB0/6BD56736/1FD6412B) wholly from
	// typed columns (#187) - the captured `Data` blob is retired. Sibling of NpcSpawnBuilder, but simpler: a world
	// object has no CharacterIIR, so the body is header (subOpcode + the class-constant typeTag) + transform (from the
	// row's columns) + identity message (from the columns, SimpleDynelMessageCodec) + KdTreeTail (the stored geometry).
	// One builder handles every object class. Byte-identical to the captured body (WorldObjectBuilderTests).
	public static class WorldObjectBuilder
	{
		// Builds a world-object body FROM SCRATCH (#249) - no captured `Data`: the header (subOpcode + typeTag +
		// handle, fieldA=0) + the transform (pos/rot + the live playfield token as the 0x18 4-byte varint) + the
		// identity message generated from the row's columns (SimpleDynelMessageCodec) + the row's KdTreeTail (the
		// static geometry). Byte-identical to a captured body when fed that object's columns + KdTreeTail + transform
		// (WorldObjectBuilderTests). This is how a loot bag is spawned with NO captured world-object row in the world
		// (the loot system's bag definition is a baked constant - LootBagTemplate). Returns false if the row has no
		// lifted columns (DisplayName) or no KdTreeTail.
		public static bool BuildBody(WorldObject row, uint handle, ProtoVec3 position, ProtoQuat rotation, uint typeTag, uint playfieldToken, out byte[] body)
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

		// The stored row's object identity - the lifted DisplayName column (was read from the captured Data; the blob
		// is retired). Used to key the table rows the same way the burst bodies are keyed.
		public static string ReadIdentity(WorldObject row)
		{
			return row?.DisplayName ?? string.Empty;
		}

		// The wire typeTag (the 0x0000C8xx EntityClass ack in the object header) for a world-object CLASS. Class-stable
		// (one distinct tag per SubOpcode across the whole capture set), so it is derived here rather than stored.
		public static uint TypeTagForClass(uint subOpcode) => subOpcode switch
		{
			0x1FD6412Bu => 0x0000C87Du, // ClimbingDynel
			0x33A56FB0u => 0x0000C748u, // ZoneLabel
			0x66AEDD50u => 0x0000C878u, // SimpleDynel
			0x6BD56736u => 0x0000C879u, // Destructible
			0xF096251Eu => 0x0000C87Au, // Container
			_ => 0x0000C878u
		};

		// Builds a world-object body from the row's TYPED COLUMNS + KdTreeTail (the retired Data blob is no longer read),
		// stamped with the live `playfieldInstance` token. This is the sole burst-less / create-editor object path.
		// Returns false when the row isn't lifted (no DisplayName / KdTreeTail).
		public static bool TryBuild(WorldObject row, uint handle, uint playfieldInstance, out byte[] body)
		{
			return BuildBody(row, handle, row?.Position ?? default, row?.Rotation ?? Quaternion.Identity, TypeTagForClass(row?.SubOpcode ?? 0), playfieldInstance, out body);
		}

		// As TryBuild but with a placeholder playfield token (0) - for diagnostics / callers that re-stamp per recipient.
		public static bool TryBuild(WorldObject row, uint handle, out byte[] body)
		{
			return TryBuild(row, handle, 0u, out body);
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

			// CONSTRUCT the identity from the row's columns + append the stored geometry tail - the captured Data blob is
			// retired. Byte-identical to the old verbatim splice (the columns reproduce the captured identity, the tail
			// is stored verbatim), so the world-entry object is fully DB-driven. Returns false for an un-lifted row (no
			// DisplayName / KdTreeTail) - it can no longer be built.
			if (row is not { DisplayName: not null, KdTreeTail: not null })
				return false;

			var identity = SimpleDynelMessageCodec.BuildMessage(SimpleDynelMessageCodec.ToMessage(row));
			body = new byte[liveOffset + identity.Length + row.KdTreeTail.Length];
			liveBody[..liveOffset].CopyTo(body);
			identity.CopyTo(body.AsSpan(liveOffset));
			row.KdTreeTail.CopyTo(body.AsSpan(liveOffset + identity.Length));
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
