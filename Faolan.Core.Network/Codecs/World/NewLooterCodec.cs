using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// NewCurrentLooterIIR (0xD06568C8) / NewAllowedLooterIIR (0x1E2E0139) - "this bag's looter is now <entity>". A
	// FAMILY (#530 rule 5a): one layout with the verb in the opcode, so a shared base is the honest model and picking
	// one of the two as *the* opcode would be a lie about the other. 21 B:
	//   [subop][0xC87A LootBag][bagHandle][u8 flag][u32 looterClass][u32 looterHandle]
	// The RESET (looter -> none, once the bag is emptied) is the same record with a NULL entity reference - class and
	// handle both 0. It used to be written as a separate "two zero words then a byte" shape, which is the same nine
	// bytes read as a different grammar.
	//
	// Retail sends the ALLOWED-looter form immediately AFTER the LootBagFullUpdateIIR at a bag's mid-session spawn
	// (capture fr1358: 1e2e0139 0000c87a <bag> 00 0000c350 <player>) - it is what makes the client INSTANTIATE +
	// render a freshly-introduced loot bag mid-session (#263); the bag's FullUpdate alone only renders on a
	// playfield (re)load.
	public static class NewLooterCodec
	{
		public static readonly GameServer0X2000Opcodes[] HandledTypes = [GameServer0X2000Opcodes.NewCurrentLooterIIR, GameServer0X2000Opcodes.NewAllowedLooterIIR];

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(NewLooterIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static NewLooterIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(NewLooterCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static NewLooterIIR Read(ref SpanReader r)
		{
			var offset = r.Position;
			var subop = (GameServer0X2000Opcodes)r.ReadU32Be();
			if (Array.IndexOf(HandledTypes, subop) < 0)
				throw CodecParseException.Unexpected(nameof(NewLooterCodec), offset, "NewCurrentLooterIIR or NewAllowedLooterIIR", $"0x{(uint)subop:X8}");

			r.ExpectU32Be(EntityClass.LootBag, "entity class");
			var bagHandle = r.ReadU32Be();
			return new NewLooterIIR(subop, bagHandle, r.ReadU8(), r.ReadU32Be(), r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, NewLooterIIR v)
		{
			s.WriteUInt32((uint)v.SubOpcode).WriteUInt32(EntityClass.LootBag).WriteUInt32(v.BagHandle).WriteByte(v.Flag).WriteUInt32(v.LooterClass).WriteUInt32(v.LooterHandle);
		}
	}

	// NewCurrentLooterIIR / NewAllowedLooterIIR. LooterClass + LooterHandle are one entity reference; (0, 0) = no
	// looter.
	public sealed record NewLooterIIR(GameServer0X2000Opcodes SubOpcode, uint BagHandle, byte Flag, uint LooterClass, uint LooterHandle);

	// VALUE factories for the two looter verbs. They live here, not as Build overloads, so a caller states WHICH
	// looter it is setting by name instead of handing the codec an IIR opcode - #530 rule 7 keeps the IIR names
	// inside Codecs/, and `NewLooterCodec.Build(GameServer0X2000Opcodes.NewAllowedLooterIIR, ...)` was the last
	// place in the server layer that spelled one.
	public static class NewLooters
	{
		// The bag's CURRENT looter - who is holding it open. `playerHandle` 0 clears it.
		public static NewLooterIIR Current(uint bagHandle, uint playerHandle)
		{
			return For(GameServer0X2000Opcodes.NewCurrentLooterIIR, bagHandle, playerHandle);
		}

		// Who is ALLOWED to loot the bag - and, mid-session, what makes the client instantiate it at all (#263).
		public static NewLooterIIR Allowed(uint bagHandle, uint playerHandle)
		{
			return For(GameServer0X2000Opcodes.NewAllowedLooterIIR, bagHandle, playerHandle);
		}

		private static NewLooterIIR For(GameServer0X2000Opcodes subOpcode, uint bagHandle, uint playerHandle)
		{
			return new NewLooterIIR(subOpcode, bagHandle, 0x00, playerHandle == 0
				? 0
				: EntityClass.Character, playerHandle);
		}
	}
}
