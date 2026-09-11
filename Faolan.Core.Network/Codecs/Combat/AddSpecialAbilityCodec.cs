using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     GameCharInterface.DispatchAddSpecialAbility - the ability grant. Twenty big-endian u32 fields followed by the
	///     announce byte:
	///     [slot][id][id][0][1][data0][key1..4][key1..4][nullIdentity][nullIdentity][trailerKind][trailerValue][announce].
	///     Byte-for-byte as retail; golden in AddSpecialAbilityCodecTests.
	/// </summary>
	/// <remarks>
	///     The final byte is the announce flag: 1 raises the "you learned a new ability" toast (first learn), 0 is a silent
	///     re-grant (login). It is the only byte that differs between the create-flow and the per-login grant of the same
	///     ability.
	///     <para>
	///         The duplicated id, the 0/1 constants, the repeated key quad and the null-identity pair are asserted on parse,
	///         so a mis-shaped body is rejected rather than yielding garbage. The trailer pair must stay unasserted: our own
	///         grants carry (1, 0), but retail also sends (1, 0x0B) and (2, 0x37F412) / (2, 0x37F298), where the value looks
	///         like a linked ability id. The importer harvests only Slot/Id/Data.
	///     </para>
	/// </remarks>
	public static class AddSpecialAbilityCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchAddSpecialAbility;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

			// Two unset identity slots, not one 16-byte constant: they follow the repeated key quad above.
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

	/// <summary>The DispatchAddSpecialAbility body.</summary>
	public sealed record AddSpecialAbility(uint Slot, uint Id, uint Data0, ProtoDecryptionKey Key, uint TrailerKind, uint TrailerValue, bool Announce);

	/// <summary>Maps a parsed <see cref="AddSpecialAbility" /> onto the <see cref="Ability" /> row it grants.</summary>
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
