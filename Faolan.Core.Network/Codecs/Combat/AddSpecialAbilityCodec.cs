using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	// GameCharInterface.DispatchAddSpecialAbility (0x2052) - the ability grant. Byte-for-byte as retail, decoded from
	// the create/combat captures (golden in AddSpecialAbilityCodecTests). 20 u32 fields + the announce byte:
	//
	//   [slot][id][id][0][1][data0][key1..4][key1..4][nullIdentity][nullIdentity][trailerKind][trailerValue][u8 announce]
	//
	// The final byte is the announce flag: 0x01 = "you learned a new ability" toast (first learn), 0x00 = silent
	// re-grant (login) - the only byte that differs between the create-flow and per-login grants for the same ability.
	//
	// The duplicated Id, the 0/1 constants, the repeated key quad and the NullIdentity pair are ASSERTED on parse, so a
	// mis-shaped body is rejected rather than yielding garbage. The TRAILER pair is not: the server's own grants carry
	// (1, 0), but captured retail grants also carry (1, 0x0B) and (2, 0x37F412) / (2, 0x37F298) - the value looks like a
	// linked ability id. Requiring (1, 0) wrongly rejected 3 of 27 valid grants when the importer switched to this
	// parser. (Modelling it is a #393-adjacent follow-up; the importer harvests only Slot/Id/Data.)
	public static class AddSpecialAbilityCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchAddSpecialAbility;

		public const int BodyLength = 81;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(AddSpecialAbility value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(Ability ability, bool announce = true)
		{
			return Build(new AddSpecialAbility(ability.Slot, ability.Id, ability.Data0, ability.Key, 1, 0, announce));
		}

		public static AddSpecialAbility Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(AddSpecialAbilityCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static AddSpecialAbility Read(ref SpanReader r)
		{
			var slot = r.ReadU32Be();
			var id = r.ReadU32Be();
			r.ExpectU32Be(id, "the repeated ability id");
			r.ExpectU32Be(0, "constant 0");
			r.ExpectU32Be(1, "constant 1");
			var data0 = r.ReadU32Be();

			var key = ReadKey(ref r);
			var repeated = ReadKey(ref r);
			if (repeated.Key1 != key.Key1 || repeated.Key2 != key.Key2 || repeated.Key3 != key.Key3 || repeated.Key4 != key.Key4)
				throw CodecParseException.Unexpected(nameof(AddSpecialAbilityCodec), r.Position - 4 * sizeof(uint), "the ability key repeated", "a different quad");

			// Two unset identity slots. They follow the repeated key above, which is what shows they are a PAIR of
			// identities rather than one 16-byte constant.
			for (var i = 0; i < 2; i++)
			{
				r.ExpectU32Be(NullIdentity.High, "null identity (high)");
				r.ExpectU32Be(NullIdentity.Low, "null identity (low)");
			}

			var trailerKind = r.ReadU32Be();
			var trailerValue = r.ReadU32Be();

			var announce = r.ReadU8();
			if (announce > 1)
				throw CodecParseException.Unexpected(nameof(AddSpecialAbilityCodec), r.Position - 1, "an announce flag of 0 or 1", $"0x{announce:X2}");

			return new AddSpecialAbility(slot, id, data0, key, trailerKind, trailerValue, announce == 1);
		}

		internal static void Write(FaolanStream s, AddSpecialAbility v)
		{
			s.WriteUInt32(v.Slot).WriteUInt32(v.Id).WriteUInt32(v.Id).WriteUInt32(0).WriteUInt32(1).WriteUInt32(v.Data0);
			for (var i = 0; i < 2; i++)
				s.WriteUInt32(v.Key.Key1).WriteUInt32(v.Key.Key2).WriteUInt32(v.Key.Key3).WriteUInt32(v.Key.Key4);

			for (var i = 0; i < 2; i++)
				s.WriteUInt32(NullIdentity.High).WriteUInt32(NullIdentity.Low);

			s.WriteUInt32(v.TrailerKind).WriteUInt32(v.TrailerValue).WriteByte(Convert.ToByte(v.Announce));
		}

		private static ProtoDecryptionKey ReadKey(ref SpanReader r)
		{
			return new ProtoDecryptionKey
			{
				Key1 = r.ReadU32Be(),
				Key2 = r.ReadU32Be(),
				Key3 = r.ReadU32Be(),
				Key4 = r.ReadU32Be()
			};
		}
	}

	// The DispatchAddSpecialAbility body.
	public sealed record AddSpecialAbility(uint Slot, uint Id, uint Data0, ProtoDecryptionKey Key, uint TrailerKind, uint TrailerValue, bool Announce);

	// AddSpecialAbility -> the `Ability` row it grants. A MAPPER, not a codec (row<->value mapping proper is #533).
	public static class AbilityColumns
	{
		public static Ability ToAbility(AddSpecialAbility v)
		{
			return new Ability
			{
				Slot = v.Slot,
				Id = v.Id,
				Data0 = v.Data0,
				Key = v.Key
			};
		}
	}
}
