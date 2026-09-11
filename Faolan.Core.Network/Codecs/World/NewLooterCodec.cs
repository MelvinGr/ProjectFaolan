using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// NewCurrentLooterIIR / NewAllowedLooterIIR - "this bag's looter is now <entity>". A family: one layout with the
	// verb in the opcode, so neither type can be named as *the* opcode without lying about the other. 21 B:
	//   [subop][0xC87A LootBag][bagHandle][u8 flag][u32 looterClass][u32 looterHandle]
	// The reset (looter -> none, once the bag is emptied) is the same record with a null entity reference - looter
	// class and handle both 0.
	//
	// Retail sends the allowed-looter form immediately after the LootBagFullUpdateIIR at a bag's mid-session spawn
	// (capture fr1358: 1e2e0139 0000c87a <bag> 00 0000c350 <player>); that is what makes the client instantiate and
	// render a freshly introduced loot bag mid-session. The bag's FullUpdate alone only renders on a playfield
	// (re)load.
	public static class NewLooterCodec
	{
		public static readonly IirTypes[] HandledTypes = [IirTypes.NewCurrentLooterIIR, IirTypes.NewAllowedLooterIIR];

		// Canonical - the only method here that writes bytes.
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
			var subop = (IirTypes)r.ReadU32Be();
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

	// LooterClass and LooterHandle are one entity reference; (0, 0) means no looter.
	public sealed record NewLooterIIR(IirTypes SubOpcode, uint BagHandle, byte Flag, uint LooterClass, uint LooterHandle);

	// Value factories for the two looter verbs. They live here, not as Build overloads, so a caller states which
	// looter it is setting by name instead of handing the codec an IIR opcode, which keeps the IIR names inside
	// Codecs/.
	public static class NewLooters
	{
		/// <summary>The bag's current looter - who is holding it open.</summary>
		/// <param name="playerHandle">0 clears the looter.</param>
		public static NewLooterIIR Current(uint bagHandle, uint playerHandle)
		{
			return For(IirTypes.NewCurrentLooterIIR, bagHandle, playerHandle);
		}

		/// <summary>Who is allowed to loot the bag - and, mid-session, what makes the client instantiate it at all.</summary>
		public static NewLooterIIR Allowed(uint bagHandle, uint playerHandle)
		{
			return For(IirTypes.NewAllowedLooterIIR, bagHandle, playerHandle);
		}

		private static NewLooterIIR For(IirTypes subOpcode, uint bagHandle, uint playerHandle)
		{
			return new NewLooterIIR(subOpcode, bagHandle, 0x00, playerHandle == 0
				? 0
				: EntityClass.Character, playerHandle);
		}
	}
}
