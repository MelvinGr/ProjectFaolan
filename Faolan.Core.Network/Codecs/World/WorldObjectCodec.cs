using System;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.World
{
	// The world-object / dynel full-update body, for every object class that carries one: SimpleDynel, LootBag, Door,
	// Destructible, ClimbingDynel and TombStone. One family, one grammar:
	//
	//   [u32 subOpcode][u32 typeTag][u32 handle][u8 env][u32 transformLen][transform][ tail ]
	//
	// The transform is the base-class ProtoGameCodeDynelIIR { position=1, rotation=2, playfield_instance=3 }; the tail
	// is the identity message (SimpleDynelIdentityCodec) followed by the geometry record and, for a container, its
	// loot contents. The u32 body length that precedes all of this belongs to the sender.
	//
	// Byte-identical to the captured body (WorldObjectBuilderTests, DynelFullUpdateCodecTests).
	public static class WorldObjectCodec
	{
		public static readonly IirTypes[] HandledTypes =
		[
			IirTypes.SimpleDynelFullUpdateIIR, IirTypes.LootBagFullUpdateIIR, IirTypes.DoorFullUpdateIIR,
			IirTypes.DestructibleFullUpdateIIR, IirTypes.ClimbingDynelFullUpdateIIR, IirTypes.TombStoneFullUpdateIIR
		];

		// Canonical - the only method here that writes bytes.
		public static byte[] Build(WorldObjectIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>
		///     Builds a world-object body from the row's typed columns, stamped with the live
		///     <paramref name="playfieldInstance" /> token. This is the sole burst-less / create-editor object path.
		/// </summary>
		/// <param name="isClosed">
		///     The object's live door state. It has no column and defaults to closed; the loaded zone passes its own
		///     LiveWorldObject.IsClosed so a door the player opened stays open.
		/// </param>
		/// <param name="currentHealth">
		///     The same idea for a destructible's health bar: null writes the captured word, which is what the round-trip
		///     and import paths want.
		/// </param>
		/// <exception cref="InvalidOperationException">
		///     The row is not lifted (it has no display element), i.e. it was only partially imported.
		/// </exception>
		public static byte[] Build(WorldObject row, uint handle, uint playfieldInstance = 0, bool isClosed = true, uint? currentHealth = null)
		{
			ArgumentNullException.ThrowIfNull(row);
			return Build(row, handle, row.Position, row.Rotation, WorldObjectColumns.TypeTagForClass(row.SubOpcode), playfieldInstance, isClosed, currentHealth);
		}

		/// <summary>
		///     As above but with the transform supplied explicitly - how a loot bag is spawned at a live drop position
		///     with no captured world-object row behind it (LootContainerTemplate).
		/// </summary>
		public static byte[] Build(WorldObject row, uint handle, ProtoVec3 position, ProtoQuat rotation, uint typeTag, uint playfieldToken, bool isClosed = true,
			uint? currentHealth = null)
		{
			return Build(ToIir(row, handle, position, rotation, typeTag, playfieldToken, isClosed, currentHealth));
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
			var subOpcode = (IirTypes)r.ReadU32Be();
			if (Array.IndexOf(HandledTypes, subOpcode) < 0)
				throw CodecParseException.Unexpected(nameof(WorldObjectCodec), offset, "a dynel-family full-update type (see HandledTypes)", $"0x{(uint)subOpcode:X8}");

			var typeTag = r.ReadU32Be();
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			var transform = r.ReadBytesU32Length().ToArray();

			// The tail - identity message, geometry record and (for a container) the loot contents - is carried verbatim
			// here. Its sub-records have their own codecs; which of them a body holds is the object class's business, and
			// WorldObjectColumns is what splits it.
			return new WorldObjectIIR((uint)subOpcode, typeTag, handle, env, transform, r.ReadRemaining().ToArray());
		}

		internal static void Write(FaolanStream s, WorldObjectIIR v)
		{
			s.WriteUInt32(v.SubOpcode).WriteUInt32(v.TypeTag).WriteUInt32(v.Handle).WriteByte(v.Env).WriteArrayUInt32Length(v.Transform).WriteArray(v.Tail);
		}

		// The row -> value mapping the delegating Build overloads use. Stays private rather than becoming a second
		// encoder.
		private static WorldObjectIIR ToIir(WorldObject row, uint handle, ProtoVec3 position, ProtoQuat rotation, uint typeTag, uint playfieldToken, bool isClosed,
			uint? currentHealth)
		{
			if (row?.DisplayKind == null)
				throw new InvalidOperationException(
					$"WorldObject '{row?.Name ?? "?"}' (id {row?.Id}) is not lifted: no display element. Re-run the importer to lift it (or Include the columns).");

			// The 0x2C transform component: a ProtoGameCodeDynelIIR { position=1, rotation=2, playfield_instance=3 }.
			// All 15787 captured FA015807/66AEDD50 groups carry exactly those three fields in that order, and every
			// captured playfield token falls in [2^21, 2^28) - where the minimal varint protobuf-net writes is already
			// 4 bytes, so SpawnTransformPatch's splice-free in-place re-address still holds.
			var transform = new ProtoGameCodeDynelIIR
			{
				Position = position,
				Rotation = rotation,
				PlayfieldInstance = playfieldToken
			}.ToProtobuf();

			var identity = SimpleDynelIdentityCodec.Build(WorldObjectColumns.ToMessage(row, isClosed));
			var geometry = WorldObjectGeometryColumns.ToWire(row, currentHealth); // the bounds record plus the per-class trailer

			// A container's trailer is its loot contents, which are session state and are not persisted: every chest
			// spawns with the same clean empty scaffold a dropped bag gets, and WithLootContents splices the live cash and
			// items per drop.
			var contents = row.SubOpcode == (uint)IirTypes.LootBagFullUpdateIIR
				? LootContainerTemplate.BuildEmptyContents()
				: [];

			return new WorldObjectIIR(row.SubOpcode, typeTag, handle, 0x00, transform, [.. identity, .. geometry, .. contents]);
		}
	}

	// One world object's full-update record. `Tail` is everything after the transform - the identity message, the
	// geometry record and (for a container) the loot contents - carried verbatim by the family codec.
	public sealed record WorldObjectIIR(uint SubOpcode, uint TypeTag, uint Handle, byte Env, byte[] Transform, byte[] Tail);
}
