using System;
using System.Buffers.Binary;
using System.IO;
using Faolan.Core.Network.Protobuf;
using ProtoBuf;

namespace Faolan.GameServer.Spawn
{
	// Typed codec for the LootBagFullUpdateIIR (0xF096251E) CONTENTS message - the loot bag's cash + looter handles +
	// item inventory (#187). The reader was decoded by vtable-xref (docs/iir-subclass-readers.md, FUN_007a3db0):
	// after the record header + transform, a loot bag is
	//   [u32 len][ProtoSimpleDynelIIR identity][kd-tree/bounds struct][u32 len][ProtoLootBagIIR contents]
	// i.e. the same identity head as a plain dynel (SimpleDynelMessageCodec) plus a SECOND length-prefixed blob -
	// the ProtoLootBagIIR - at the very end of the body. This codec lifts that trailing blob into the typed
	// ProtoLootBagIIR (cash / current_looter / owner / dropper / inventory) and rebuilds it BYTE-EXACT (protobuf-net
	// round-trips the captured wire bytes losslessly, including the nested ProtoInventoryGame - LootBagMessageTests).
	//
	// This makes a loot bag's contents CONSTRUCTIBLE from the live drop (cash + looter + the killed entity's items)
	// instead of replaying a captured template's contents. NB: flipping MakeCorpseLootable to send constructed
	// contents is in-client-gated - changing a loot bag's on-wire contents mid-session is the substitution risk
	// noted in the spawn-render memory; this codec is the proven building block for when that's verified.
	public static class LootBagMessageCodec
	{
		// LootBagFullUpdateIIR sub-opcode.
		public const uint SubOpcode = 0xF096251E;

		// Serializes a typed ProtoLootBagIIR to the on-wire trailing blob: [u32 BE len][protobuf].
		public static byte[] BuildContents(ProtoLootBagIIR contents)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, contents);
			var protobuf = ms.ToArray();

			var outBytes = new byte[4 + protobuf.Length];
			BinaryPrimitives.WriteUInt32BigEndian(outBytes.AsSpan(0, 4), (uint)protobuf.Length);
			protobuf.CopyTo(outBytes, 4);
			return outBytes;
		}

		// Constructs a ProtoLootBagIIR from the live drop: the cash on the corpse and its dropped items (already an
		// assembled ProtoInventoryGame, built the same way the backpack snapshot is). Looter/owner default to "no
		// owner" ({0,0}) - free-for-all loot, matching the captured bags. The empty-id messages are emitted (the
		// captured bags carry them), so this round-trips to the captured shape when fed the captured cash/inventory.
		public static ProtoLootBagIIR BuildLootBag(uint cash, ProtoInventoryGame inventory)
		{
			return new ProtoLootBagIIR
			{
				Cash = cash,
				CurrentLooterId = new ProtoID32
				{
					Type = 0,
					Instance = 0
				},
				OwnerId = new ProtoID32
				{
					Type = 0,
					Instance = 0
				},
				DropperId = new ProtoID32
				{
					Type = 0,
					Instance = 0
				},
				AllowedLooterId = new ProtoID32
				{
					Type = 0,
					Instance = 0
				},
				HasMasterLooter = false,
				Inventory = inventory
			};
		}

		// Locates and parses the trailing ProtoLootBagIIR out of a loot-bag IIR record body (the bytes after the
		// 0x2000 carrier's [u32 len][u32 iirType] prefix). The contents blob is the FINAL length-prefixed message:
		// its [u32 len] is the unique offset where len + 4 consumes exactly to the end of the record. Returns false
		// if no such framing exists. `contentsStart` is the offset of the [u32 len] prefix.
		public static bool TryParse(ReadOnlySpan<byte> record, out ProtoLootBagIIR contents, out int contentsStart)
		{
			contents = null;
			contentsStart = 0;
			for (var k = 0; k + 4 <= record.Length; k++)
			{
				var len = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(k, 4));
				if (len == 0 || k + 4 + len != record.Length)
					continue;

				contents = Serializer.Deserialize<ProtoLootBagIIR>(record.Slice(k + 4, (int)len).ToArray().AsMemory());
				contentsStart = k;
				return true;
			}

			return false;
		}
	}
}
