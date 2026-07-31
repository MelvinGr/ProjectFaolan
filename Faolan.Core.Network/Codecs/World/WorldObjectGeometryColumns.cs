using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// The bounds/geometry record that follows a world object's identity message - the client's shared reader
	// FUN_0080c620, decoded in docs/re/iir-readers-combat-world.md and verified against all 1052 captured rows.
	// This is what the `KdTreeTail` blob used to hold verbatim (#410); the record is now built from typed columns:
	//
	//   [u32 assetId][u32 assetType]     an RDB resource reference - 1010030 MESHINDEXFILE / 1010001 RenderMesh /
	//                                    1010201 MONSTERDATA, nothing else in the corpus
	//   [u32 nameLen][name]              length-prefixed ASCII; empty in 1029/1052 rows ("object_closed" otherwise)
	//   [u32 nodeCount][node x 6 u32]    24 bytes per node; EMPTY in 1028/1052 rows, exactly one node in the rest
	//   [f32 min xyz][f32 max xyz]       the AABB (the client derives centre/extent as (max-min)*0.5)
	//   [u32][u32][u8 flag]
	//
	// followed by the PER-CLASS trailer, which is what makes the record's length vary by object class: none for
	// SimpleDynel, 5 u32 for Destructible, 1 for Door and Climbing. Those are WorldObjectWord rows.
	//
	// LootBag (chests) is the exception and is NOT handled here: its trailer is the container's loot CONTENTS
	// ([u32 len][ProtoLootBagIIR]), which is session state, not placement truth - the captured contents are
	// deliberately not persisted and WorldObjectCodec appends a clean empty scaffold instead (LootContainerTemplate,
	// which already worked this way for spawned bags).
	// NOT a codec, deliberately: this is the WorldObject COLUMNS <-> that region mapping, and the region's length
	// depends on the row's object class (the per-class trailer below), so it is not a standalone message with a
	// self-describing body. Row<->value mapping is #533's subject; #530 rule 1 leaves it named for what it is.
	// NOT a codec, deliberately (#530 rule 1): this is the WorldObject COLUMNS <-> that region mapping, and the
	// region's length depends on the row's object CLASS (the per-class trailer below), so it is not a standalone
	// message with a self-describing body. Row<->value mapping is #533's subject.
	public static class WorldObjectGeometryColumns
	{
		// An object with no bounds writes an INVERTED box rather than zeros: min = +FLT_MAX, max = -FLT_MAX. Kept as
		// the actual float values in the columns (no sentinel translation) so the record round-trips bit-for-bit.
		public static byte[] ToWire(WorldObject row)
		{
			var name = row.GeometryName ?? "";
			var nodes = row.Words.Where(w => w.Kind == WorldObjectWordKind.GeometryNode).OrderBy(w => w.Ordinal).Select(w => w.Value).ToArray();
			if (nodes.Length % 6 != 0)
				throw new InvalidOperationException($"WorldObject {row.Id} has {nodes.Length} geometry-node word(s), which is not a whole number of 6-word nodes.");

			using var stream = new FaolanStream();
			stream.WriteUInt32(row.GeometryAssetId)
				.WriteUInt32(row.GeometryAssetType)
				.WriteUInt32((uint)name.Length)
				.WriteArray(Encoding.Latin1.GetBytes(name))
				.WriteUInt32((uint)(nodes.Length / 6));

			foreach (var word in nodes)
				stream.WriteUInt32(word);

			stream
				.WriteFloat(row.BoundsMinX)
				.WriteFloat(row.BoundsMinY)
				.WriteFloat(row.BoundsMinZ)
				.WriteFloat(row.BoundsMaxX)
				.WriteFloat(row.BoundsMaxY)
				.WriteFloat(row.BoundsMaxZ)
				.WriteUInt32(row.GeometryWord0)
				.WriteUInt32(row.GeometryWord1)
				.WriteByte(row.GeometryFlag
					? (byte)1
					: (byte)0);

			foreach (var word in row.Words.Where(w => w.Kind == WorldObjectWordKind.Trailer).OrderBy(w => w.Ordinal))
				stream.WriteUInt32(word.Value);

			return stream.ToArray();
		}

		// Fills the row's geometry columns + word rows from the captured record, and reports how many bytes it
		// consumed - the caller re-Builds and compares, so a shape this grammar does not cover cannot be
		// half-imported. `consumed` is short of `tail.Length` exactly for LootBag, whose remaining bytes are the
		// captured loot contents (dropped on purpose, see the class comment).
		public static bool TryParse(WorldObject row, ReadOnlySpan<byte> tail, out int consumed)
		{
			consumed = 0;
			if (tail.Length < 8)
				return false;

			row.GeometryAssetId = tail.ReadU32Be(0);
			row.GeometryAssetType = tail.ReadU32Be(4);

			var offset = 8;
			if (!TryReadUInt32(tail, ref offset, out var nameLength) || nameLength > 256 || offset + (int)nameLength > tail.Length)
				return false;

			row.GeometryName = nameLength == 0
				? null
				: Encoding.Latin1.GetString(tail.Slice(offset, (int)nameLength));
			offset += (int)nameLength;

			if (!TryReadUInt32(tail, ref offset, out var nodeCount) || nodeCount > 4096)
				return false;

			var words = new List<WorldObjectWord>();
			for (var i = 0; i < nodeCount * 6; i++)
			{
				if (!TryReadUInt32(tail, ref offset, out var value))
					return false;

				words.Add(new WorldObjectWord
				{
					Kind = WorldObjectWordKind.GeometryNode,
					Ordinal = i,
					Value = value
				});
			}

			if (offset + 33 > tail.Length)
				return false;

			row.BoundsMinX = ReadFloat(tail, ref offset);
			row.BoundsMinY = ReadFloat(tail, ref offset);
			row.BoundsMinZ = ReadFloat(tail, ref offset);
			row.BoundsMaxX = ReadFloat(tail, ref offset);
			row.BoundsMaxY = ReadFloat(tail, ref offset);
			row.BoundsMaxZ = ReadFloat(tail, ref offset);
			TryReadUInt32(tail, ref offset, out var word0);
			TryReadUInt32(tail, ref offset, out var word1);
			row.GeometryWord0 = word0;
			row.GeometryWord1 = word1;
			row.GeometryFlag = tail[offset++] != 0;

			// The per-class trailer. LootBag's is the loot contents and is not ours to keep.
			var trailerWords = TrailerWordCount(row.SubOpcode);
			for (var i = 0; i < trailerWords; i++)
			{
				if (!TryReadUInt32(tail, ref offset, out var value))
					return false;

				words.Add(new WorldObjectWord
				{
					Kind = WorldObjectWordKind.Trailer,
					Ordinal = i,
					Value = value
				});
			}

			row.Words.Clear();
			foreach (var word in words)
				row.Words.Add(word);

			consumed = offset;
			return true;
		}

		// How many fixed u32 the class appends after the geometry record, per docs/re/iir-readers-combat-world.md
		// (measured: 678 SimpleDynel rows with none, 251 Destructible with 5, 59 Door and 49 Climbing with 1).
		// LootBag appends its loot contents instead, which is why it is 0 here and short-parses by design.
		private static int TrailerWordCount(uint subOpcode)
		{
			return (GameServer0X2000Opcodes)subOpcode switch
			{
				GameServer0X2000Opcodes.DestructibleFullUpdateIIR => 5,
				GameServer0X2000Opcodes.DoorFullUpdateIIR => 1,
				GameServer0X2000Opcodes.ClimbingDynelFullUpdateIIR => 1,
				_ => 0 // SimpleDynelFullUpdateIIR has none; LootBagFullUpdateIIR carries contents, not words
			};
		}

		private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
		{
			if (offset + 4 > data.Length)
			{
				value = 0;
				return false;
			}

			value = data.ReadU32Be(offset);
			offset += 4;
			return true;
		}

		private static float ReadFloat(ReadOnlySpan<byte> data, ref int offset)
		{
			var value = BitConverter.Int32BitsToSingle(data.ReadI32Be(offset));
			offset += 4;
			return value;
		}
	}
}
