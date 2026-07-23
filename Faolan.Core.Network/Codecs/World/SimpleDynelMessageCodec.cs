using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using ProtoBuf;

namespace Faolan.Core.Network.Codecs.World
{
	// Typed codec for the SimpleDynelFullUpdateIIR (0x66AEDD50) identity MESSAGE - the first "construct, don't
	// replay" step for world dynels (#187 / #57). The reader was decoded by VTABLE-xref (docs/re/iir-subclass-readers.md,
	// FUN_007a2510 -> FUN_00cbfb80 ReadLengthPrefixedBlob): after the record header + the base-class transform, the
	// dynel body is `[u32 BE blobLen][ProtoSimpleDynelIIR protobuf][kd-tree/bounds struct]`. This codec lifts that
	// length-prefixed protobuf out of the opaque `Data` slice into the typed `ProtoSimpleDynelIIR` (name /
	// display_name / cars_group / template_item_id / flags) and rebuilds it BYTE-EXACT (protobuf-net round-trips the
	// captured wire bytes losslessly - SimpleDynelMessageTests). The kd-tree/bounds tail after it stays verbatim for
	// now (its data is RDB geometry, not yet in the DB).
	public static class SimpleDynelMessageCodec
	{
		// SimpleDynelFullUpdateIIR sub-opcode (the object class that carries a ProtoSimpleDynelIIR identity).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.SimpleDynelFullUpdateIIR; // 0x66AEDD50

		// SimpleDynelVisualChangeIIR (0xE7F4F8F8): flips a SimpleDynel's visual/collision STATE. Retail sends it to the
		// Casilda chain decor (this class) the instant she's freed on the beach, switching it to state 5 - which drops the
		// chain's COLLISION = the "invisible wall" (#267; the hangingwoman mesh doesn't render for us, so the dynel reads
		// as an invisible barrier). Decoded byte-exact from Hoxxling gameplay idx 2053 (21 B):
		//   [subop 0xE7F4F8F8][typeTag 0xC878][u32 handle][u32 0][u8 state][u32 0]
		public const uint VisualChangeOpcode = (uint)GameServer0X2000Opcodes.SimpleDynelVisualChangeIIR; // 0xE7F4F8F8

		public const uint VisualChangeTypeTag = 0x0000C878;

		// The record header before the transform: [u32 type][u32 handle][u32 0].
		private const int HeaderLength = 12;

		// Every IIR type this codec constructs or parses (Opcode is the primary). Declared for the same reason
		// DynelFullUpdateCodec does: a grep for the opcode member finds its emitter, and the coverage matrix reads it.
		public static readonly GameServer0X2000Opcodes[] HandledTypes = [GameServer0X2000Opcodes.SimpleDynelFullUpdateIIR, GameServer0X2000Opcodes.SimpleDynelVisualChangeIIR];

		// Copies a parsed identity message into a WorldObject row's columns (the importer path). The nullable columns
		// stay null when the field is absent, so ToMessage round-trips back to the same wire bytes.
		public static void ApplyColumns(WorldObject row, ProtoSimpleDynelIIR message)
		{
			row.Kdtree = message.Kdtree;
			row.Name = message.Name;
			row.DisplayName = message.DisplayName;
			row.CarsGroup = message.CarsGroup;
			row.IsClosed = message.IsClosed;
			row.QuestItemEffect = message.ShouldSerializeQuestItemEffect()
				? message.QuestItemEffect
				: null;
			row.WaypointType = message.ShouldSerializeWaypointType()
				? message.WaypointType
				: null;
			row.TemplateItemId = message.ShouldSerializeTemplateItemId()
				? message.TemplateItemId
				: null;
		}

		// Serializes a typed ProtoSimpleDynelIIR to the on-wire identity message: [u32 BE blobLen][protobuf]. Byte-exact
		// to the captured slice for a round-tripped message (protobuf-net is lossless on these records).
		public static byte[] BuildMessage(ProtoSimpleDynelIIR message)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, message);
			var protobuf = ms.ToArray();

			var outBytes = new byte[4 + protobuf.Length];
			BinaryPrimitives.WriteUInt32BigEndian(outBytes.AsSpan(0, 4), (uint)protobuf.Length);
			protobuf.CopyTo(outBytes, 4);
			return outBytes;
		}

		public static byte[] BuildVisualChange(uint handle, byte state)
		{
			return new FaolanStream().WriteUInt32(VisualChangeOpcode).WriteUInt32(VisualChangeTypeTag).WriteUInt32(handle).WriteUInt32(0).WriteByte(state).WriteUInt32(0).ToArray();
		}

		// Builds the typed identity message from a WorldObject row's columns (the DB-source path). Kdtree is always 0
		// on world dynels; the two nullable columns are emitted only when present, reproducing the captured field-set.
		// Stays here, not on WorldObject: the entity is a plain Core.Database row, and wire/protobuf construction is
		// the codec's job - putting it on the row would couple the DB layer to the protobuf message type.
		public static ProtoSimpleDynelIIR ToMessage(WorldObject row)
		{
			var message = new ProtoSimpleDynelIIR
			{
				Kdtree = row.Kdtree,
				Name = row.Name,
				DisplayName = row.DisplayName,
				CarsGroup = row.CarsGroup,
				IsClosed = row.IsClosed
			};
			if (row.QuestItemEffect.HasValue)
				message.QuestItemEffect = row.QuestItemEffect.Value;
			if (row.WaypointType.HasValue)
				message.WaypointType = row.WaypointType.Value;
			if (row.TemplateItemId.HasValue)
				message.TemplateItemId = row.TemplateItemId.Value;
			return message;
		}

		// Lifts the identity columns + geometry tail from the captured object `body` (the importer path; `body` is the
		// FA015807-style object body extracted from the dump packet). Self-validating: populates the typed columns +
		// KdTreeTail only if the column-rebuild reproduces the record byte-exact, else leaves DisplayName null (the row
		// then can't build - zero risk for any shape not fully modelled). Idempotent. Returns true if the row was lifted.
		public static bool TryLiftColumns(WorldObject row, byte[] body)
		{
			row.DisplayName = null;
			row.KdTreeTail = null;
			if (body is not { Length: > 8 } || !TryParse(body.AsSpan(8), out var message, out var messageStart, out var consumed))
				return false;

			ApplyColumns(row, message);
			if (TryReplaceIdentityFromColumns(body.AsSpan(8), row, out var rebuilt) && rebuilt.AsSpan().SequenceEqual(body.AsSpan(8)))
			{
				// Store the geometry tail (everything after the identity message) so the build needs no Data slice.
				row.KdTreeTail = body.AsSpan(8)[(messageStart + consumed)..].ToArray();
				return true;
			}

			row.DisplayName = null; // un-faithful lift -> fall back to the verbatim slice
			return false;
		}

		// Locates the identity message's u32 length-prefix within a SimpleDynel IIR record body (the bytes after the
		// 0x2000 carrier's [u32 len][u32 iirType] prefix): skip the 12-byte header, then the base-class transform
		// (a varint-length-delimited ProtoGameCodeDynelIIR sub-message). Returns false if the framing is malformed.
		public static bool TryLocateMessage(ReadOnlySpan<byte> record, out int offset)
		{
			offset = 0;
			var i = HeaderLength;
			if (record.Length <= i)
				return false;

			// transform = [varint len][len bytes]
			if (!TryReadVarint(record, ref i, out var transformLen))
				return false;

			i += (int)transformLen;
			if (i + 4 > record.Length)
				return false;

			offset = i;
			return true;
		}

		// Parses the typed ProtoSimpleDynelIIR out of a SimpleDynel IIR record body. `consumed` is the number of bytes
		// the message occupies (the 4-byte length prefix + the protobuf), so the caller knows where the kd-tree tail
		// begins. Returns false if the record can't be framed.
		public static bool TryParse(ReadOnlySpan<byte> record, [NotNullWhen(true)] out ProtoSimpleDynelIIR? message, out int messageStart, out int consumed)
		{
			message = null;
			messageStart = 0;
			consumed = 0;
			if (!TryLocateMessage(record, out var offset))
				return false;

			var blobLen = (int)BinaryPrimitives.ReadUInt32BigEndian(record.Slice(offset, 4));
			if (offset + 4 + blobLen > record.Length)
				return false;

			var blob = record.Slice(offset + 4, blobLen).ToArray();
			message = Serializer.Deserialize<ProtoSimpleDynelIIR>(blob.AsMemory());
			messageStart = offset;
			consumed = 4 + blobLen;
			return true;
		}

		// Replaces the identity message inside a built dynel body with one constructed from the WorldObject's columns,
		// keeping the header + transform + kd-tree tail. Byte-identical to the original when the columns match the
		// captured message (proven for every captured field-shape). Returns false (and leaves `result` null) if the
		// body can't be framed as a dynel or the row has no lifted identity (DisplayName null = not a dynel row).
		public static bool TryReplaceIdentityFromColumns(ReadOnlySpan<byte> body, WorldObject row, [NotNullWhen(true)] out byte[]? result)
		{
			result = null;
			if (row?.DisplayName == null || !TryParse(body, out _, out var start, out var consumed))
				return false;

			var rebuilt = BuildMessage(ToMessage(row));
			result = new byte[start + rebuilt.Length + (body.Length - start - consumed)];
			body[..start].CopyTo(result);
			rebuilt.CopyTo(result.AsSpan(start));
			body[(start + consumed)..].CopyTo(result.AsSpan(start + rebuilt.Length));
			return true;
		}

		// Bounded varint read over a span cursor: returns false on truncation instead of throwing (FaolanStream.ReadVarint
		// throws), so the caller can bail gracefully mid-parse of an untrusted body.
		private static bool TryReadVarint(ReadOnlySpan<byte> b, ref int i, out ulong value)
		{
			value = 0;
			var shift = 0;
			while (i < b.Length)
			{
				var x = b[i++];
				value |= (ulong)(x & 0x7f) << shift;
				if ((x & 0x80) == 0)
					return true;

				shift += 7;
			}

			return false;
		}
	}
}
