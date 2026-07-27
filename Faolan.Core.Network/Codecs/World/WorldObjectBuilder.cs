using System;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.World
{
	// Builds a stored WorldObject row into a wire body (66AEDD50/F096251E/33A56FB0/6BD56736/1FD6412B) wholly from
	// typed columns (#187) - the captured `Data` blob is retired. Sibling of NpcSpawnBuilder, but simpler: a world
	// object has no CharacterIIR, so the body is header (subOpcode + the class-constant typeTag) + transform (from the
	// row's columns) + identity message (from the columns, SimpleDynelMessageCodec) + the geometry record (from the
	// columns too, WorldObjectGeometryCodec).
	// One builder handles every object class. Byte-identical to the captured body (WorldObjectBuilderTests).
	public static class WorldObjectBuilder
	{
		// Builds a world-object body from the row's TYPED COLUMNS (the retired Data blob is no longer read),
		// stamped with the live `playfieldInstance` token. This is the sole burst-less / create-editor object path.
		// THROWS when the row isn't lifted (no DisplayName) - see BuildBody.
		public static byte[] Build(WorldObject row, uint handle, uint playfieldInstance)
		{
			return BuildBody(row, handle, row?.Position ?? default, row?.Rotation ?? Quaternion.Identity, TypeTagForClass(row?.SubOpcode ?? 0), playfieldInstance);
		}

		// As Build but with a placeholder playfield token (0) - for diagnostics / callers that re-stamp per recipient.
		public static byte[] Build(WorldObject row, uint handle)
		{
			return Build(row, handle, 0u);
		}

		// Builds a world-object body FROM SCRATCH (#249) - no captured `Data`: the header (subOpcode + typeTag +
		// handle, fieldA=0) + the transform (pos/rot + the live playfield token as the 0x18 4-byte varint) + the
		// identity message generated from the row's columns (SimpleDynelMessageCodec) + the geometry record built from
		// its geometry columns. Byte-identical to a captured body when fed that object's columns + transform
		// (WorldObjectBuilderTests). This is how a loot bag is spawned with NO captured world-object row in the world
		// (the loot system's bag definition is a baked constant - LootBagTemplate). THROWS if the row has no lifted
		// columns (DisplayName) - an un-lifted / partially-imported row, not a normal outcome.
		public static byte[] BuildBody(WorldObject row, uint handle, ProtoVec3 position, ProtoQuat rotation, uint typeTag, uint playfieldToken)
		{
			if (row?.DisplayKind == null)
				throw new InvalidOperationException(
					$"WorldObject '{row?.Name ?? "?"}' (id {row?.Id}) is not lifted: no display element. Re-run the importer to lift it (or Include the columns).");

			var head = new FaolanStream()
				.WriteUInt32(0) // body length placeholder @0 (set below)
				.WriteUInt32(row.SubOpcode)
				.WriteUInt32(typeTag)
				.WriteUInt32(handle)
				.WriteUInt32(0) // fieldA
				// The 0x2C transform component: a ProtoGameCodeDynelIIR { position=1, rotation=2, playfield_instance=3 }.
				// All 15787 captured FA015807/66AEDD50 groups carry exactly those three fields in that order, and every
				// captured playfield token falls in [2^21, 2^28) - where the MINIMAL varint protobuf-net writes is
				// already 4 bytes, so SpawnPlayfieldRewriter's splice-free in-place re-address still holds.
				.WriteByte(0x2C)
				.WriteArray(new ProtoGameCodeDynelIIR
				{
					Position = position,
					Rotation = rotation,
					PlayfieldInstance = playfieldToken
				}.ToProtobuf())
				.ToArray();

			var identity = SimpleDynelMessageCodec.BuildMessage(SimpleDynelMessageCodec.ToMessage(row)); // [u32 len][record]
			var geometry = WorldObjectGeometryCodec.Build(row); // the bounds record + per-class trailer (#410)

			// A container's trailer is its loot CONTENTS, and the captured contents are not persisted (session state,
			// #410) - every chest spawns with the same clean empty scaffold a dropped bag gets, and WithLootContents
			// splices the live cash + items per drop.
			var contents = row.SubOpcode == LootContainerTemplate.Opcode
				? LootContainerTemplate.BuildEmptyContents()
				: [];

			var body = new byte[head.Length + identity.Length + geometry.Length + contents.Length];
			head.CopyTo(body, 0);
			identity.CopyTo(body, head.Length);
			geometry.CopyTo(body, head.Length + identity.Length);
			contents.CopyTo(body, head.Length + identity.Length + geometry.Length);
			body.WriteU32Be(0, (uint)(body.Length - 4));
			return body;
		}

		// The captured `Data` is the full Conan packet ([u32 len][u32 crc][u32 headerLen][header][body]); the
		// object body starts after the 12-byte prefix + the header. Returns null if the framing is malformed.
		// Public so the importer can lift the dynel identity columns from the same body the builder consumes.
		public static byte[]? ExtractBody(byte[] data)
		{
			if (data == null || data.Length < 12)
				return null;

			var headerLen = (int)data.ReadU32Be(8);
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

			var subOpcode = carrierBody.ReadU32Be(4);
			return subOpcode == SimpleDynelMessageCodec.Opcode && SimpleDynelMessageCodec.TryParse(carrierBody.Slice(8), out var message, out _, out _)
				? message.DisplayName ?? string.Empty
				: string.Empty;
		}

		// The stored row's object identity - its display element, rebuilt from the columns (was the lifted
		// DisplayName string, and before that the captured blob; both retired). Used to key the table rows the same
		// way the burst bodies are keyed.
		public static string ReadIdentity(WorldObject row)
		{
			return row == null
				? string.Empty
				: WorldObjectDisplay.Build(row) ?? string.Empty;
		}

		// The wire typeTag (the 0x0000C8xx EntityClass ack in the object header) for a world-object CLASS. Class-stable
		// (one distinct tag per SubOpcode across the whole capture set), so it is derived here rather than stored.
		public static uint TypeTagForClass(uint subOpcode)
		{
			return subOpcode switch
			{
				(uint)GameServer0X2000Opcodes.ClimbingDynelFullUpdateIIR => EntityClass.ClimbingDynel,
				(uint)GameServer0X2000Opcodes.DoorFullUpdateIIR          => EntityClass.ZoneLabel,
				(uint)GameServer0X2000Opcodes.SimpleDynelFullUpdateIIR   => EntityClass.SimpleDynel,
				(uint)GameServer0X2000Opcodes.DestructibleFullUpdateIIR  => EntityClass.Destructible,
				(uint)GameServer0X2000Opcodes.LootBagFullUpdateIIR       => EntityClass.LootBag,
				_                                                        => EntityClass.SimpleDynel
			};
		}
	}
}
