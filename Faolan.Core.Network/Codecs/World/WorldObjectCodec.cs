using System;
using System.Linq;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.World
{
	// The world-object / dynel FULL UPDATE body, for every object class that carries one (#187): SimpleDynel
	// (0x66AEDD50), LootBag (0xF096251E), Door (0x33A56FB0), Destructible (0x6BD56736), ClimbingDynel (0x1FD6412B)
	// and TombStone (0x0D80E7C9). One FAMILY, one grammar (#530 rule 5a):
	//
	//   [u32 subOpcode][u32 typeTag][u32 handle][u8 env][u32 transformLen][transform][ tail ]
	//
	// The transform is the base-class ProtoGameCodeDynelIIR { position=1, rotation=2, playfield_instance=3 }; the tail
	// is the identity message (SimpleDynelIdentityCodec) followed by the geometry record and, for a container, its
	// loot contents. The u32 body length that precedes all of this belongs to the SENDER (#530 rule 2).
	//
	// This MERGES two emitters of the same six IIRs: WorldObjectBuilder (which built the body from a WorldObject row's
	// typed columns) and DynelFullUpdateCodec (which parsed/rebuilt the same body with the blobs opaque). They wrote
	// the same bytes through different framings - the builder's `[u32 fieldA=0][u8 0x2C]` is the codec's
	// `[u8 env=0][u32 transformLen=44]`, the same five bytes read two ways - which is exactly the duplicate the
	// one-IIR-one-codec rule exists to surface. Byte-identical to the captured body (WorldObjectBuilderTests,
	// DynelFullUpdateCodecTests).
	public static class WorldObjectCodec
	{
		public static readonly GameServer0X2000Opcodes[] HandledTypes =
		[
			GameServer0X2000Opcodes.SimpleDynelFullUpdateIIR, GameServer0X2000Opcodes.LootBagFullUpdateIIR, GameServer0X2000Opcodes.DoorFullUpdateIIR,
			GameServer0X2000Opcodes.DestructibleFullUpdateIIR, GameServer0X2000Opcodes.ClimbingDynelFullUpdateIIR, GameServer0X2000Opcodes.TombStoneFullUpdateIIR
		];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(WorldObjectIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// Builds a world-object body from the row's TYPED COLUMNS (the retired `Data` blob is no longer read),
		// stamped with the live `playfieldInstance` token. This is the sole burst-less / create-editor object path.
		// THROWS when the row isn't lifted (no display element) - an un-lifted / partially-imported row, not a normal
		// outcome.
		public static byte[] Build(WorldObject row, uint handle, uint playfieldInstance = 0)
		{
			ArgumentNullException.ThrowIfNull(row);
			return Build(row, handle, row.Position, row.Rotation, WorldObjectColumns.TypeTagForClass(row.SubOpcode), playfieldInstance);
		}

		// As above but with the transform supplied explicitly - how a loot bag is spawned at a live drop position with
		// no captured world-object row behind it (LootContainerTemplate).
		public static byte[] Build(WorldObject row, uint handle, ProtoVec3 position, ProtoQuat rotation, uint typeTag, uint playfieldToken)
		{
			return Build(ToIir(row, handle, position, rotation, typeTag, playfieldToken));
		}

		public static WorldObjectIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(WorldObjectCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static WorldObjectIIR Read(ref SpanReader r)
		{
			var offset = r.Position;
			var subOpcode = (GameServer0X2000Opcodes)r.ReadU32Be();
			if (Array.IndexOf(HandledTypes, subOpcode) < 0)
				throw CodecParseException.Unexpected(nameof(WorldObjectCodec), offset, "a dynel-family full-update type (see HandledTypes)", $"0x{(uint)subOpcode:X8}");

			var typeTag = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var transform = r.ReadBytesU32Length().ToArray();

			// The tail - identity message, geometry record and (for a container) the loot contents - is carried
			// verbatim here. Its sub-records have their own codecs; which of them a body holds is the object CLASS's
			// business, and WorldObjectColumns is what splits it.
			return new WorldObjectIIR((uint)subOpcode, typeTag, handle, env, transform, r.ReadRemaining().ToArray());
		}

		internal static void Write(FaolanStream s, WorldObjectIIR v)
		{
			s.WriteUInt32(v.SubOpcode).WriteUInt32(v.TypeTag).WriteUInt32(v.Handle).WriteByte(v.Env).WriteArrayUInt32Length(v.Transform).WriteArray(v.Tail);
		}

		// The row -> value mapping the delegating Build overloads use. Stays private (and #533-shaped) rather than
		// becoming a second encoder.
		private static WorldObjectIIR ToIir(WorldObject row, uint handle, ProtoVec3 position, ProtoQuat rotation, uint typeTag, uint playfieldToken)
		{
			if (row?.DisplayKind == null)
				throw new InvalidOperationException(
					$"WorldObject '{row?.Name ?? "?"}' (id {row?.Id}) is not lifted: no display element. Re-run the importer to lift it (or Include the columns).");

			// The 0x2C transform component: a ProtoGameCodeDynelIIR { position=1, rotation=2, playfield_instance=3 }.
			// All 15787 captured FA015807/66AEDD50 groups carry exactly those three fields in that order, and every
			// captured playfield token falls in [2^21, 2^28) - where the MINIMAL varint protobuf-net writes is
			// already 4 bytes, so SpawnPlayfieldRewriter's splice-free in-place re-address still holds.
			var transform = new ProtoGameCodeDynelIIR
			{
				Position = position,
				Rotation = rotation,
				PlayfieldInstance = playfieldToken
			}.ToProtobuf();

			var identity = SimpleDynelIdentityCodec.Build(WorldObjectColumns.ToMessage(row));
			var geometry = WorldObjectGeometryColumns.ToWire(row); // the bounds record + per-class trailer (#410)

			// A container's trailer is its loot CONTENTS, and the captured contents are not persisted (session state,
			// #410) - every chest spawns with the same clean empty scaffold a dropped bag gets, and WithLootContents
			// splices the live cash + items per drop.
			var contents = row.SubOpcode == (uint)GameServer0X2000Opcodes.LootBagFullUpdateIIR
				? LootContainerTemplate.BuildEmptyContents()
				: [];

			return new WorldObjectIIR(row.SubOpcode, typeTag, handle, 0x00, transform, [.. identity, .. geometry, .. contents]);
		}
	}

	// One world object's full-update record. `Tail` is everything after the transform - the identity message, the
	// geometry record and (for a container) the loot contents - carried verbatim by the family codec.
	public sealed record WorldObjectIIR(uint SubOpcode, uint TypeTag, uint Handle, byte Env, byte[] Transform, byte[] Tail);
}
