using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// SetNameIIR (0xADCE0CDA) - sets an entity's custom name. ONE grammar, recovered byte-exact from
	// n3InfoItemRemote::Construct (FUN_005b9d30) + SetNameIIR_t::ReadSubClass (FUN_007ae1d0), #363:
	//
	//   [u32 subop][u32 class][u32 handle][u8 isToBePassed]   <- Construct (isToBePassed must be 0 or 1)
	//   [u32 nameType][u8 flag][u8 refresh]                   <- ReadSubClass (this+0x48 u32, this+0x4c/0x4e bools)
	//   [u32 nameLen][ASCII name]                             <- nameLen is a U32, HARD-CAPPED at 0x20 by the client
	//
	// It used to be TWO builders that thought they were two messages. The create-editor's "name accepted" record was
	// written as `[u32 0][u8 0x03][u8 0x01][u32 0][u16 len][name]` - which is the SAME bytes read one field boundary
	// off: its leading zero word is `isToBePassed` plus the top three bytes of `nameType`, its `[u32 0][u16 len]` is
	// `refresh=0` plus a u32 length. The only real difference between the editor's record and the live one is that
	// last bool.
	//
	// `nameType` (this+0x48) is applied by Activate -> FUN_006ed100(nameType, this+0x4e): it MUST be 1..4 (0 hits the
	// switch default and the name is silently DROPPED), and 3 or 4 with `refresh` set also REFRESH the live nameplate
	// (FUN_006dbc00/FUN_00447ba0) - an entity's OverheadText is built from the LDB hash at spawn and only re-reads the
	// custom name on that refresh. So 3/1/1 is what makes an already-spawned NPC show the name.
	//
	// The u32 carrier length belongs to the SENDER (#530 rule 2).
	public static class SetNameCodec
	{
		public const uint Opcode = (uint)IirTypes.SetNameIIR;

		// The client reads nameLen as a u32 and HARD-CAPS it at 0x20 (so 1..31 characters); a longer name makes it
		// read length 0, skip the name and reject the blob.
		public const int MaxNameLength = 31;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SetNameIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static SetNameIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SetNameCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SetNameIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SetNameIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var isToBePassed = r.ReadU8();
			var nameType = r.ReadU32Be();
			var flag = r.ReadU8();
			var refresh = r.ReadU8();
			return new SetNameIIR(handle, Encoding.Latin1.GetString(r.ReadBytesU32Length()), nameType, flag, refresh, isToBePassed);
		}

		internal static void Write(FaolanStream s, SetNameIIR v)
		{
			s
				.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Handle)
				.WriteByte(v.IsToBePassed)
				.WriteUInt32(v.NameType)
				.WriteByte(v.Flag)
				.WriteByte(v.Refresh)
				.WriteArrayUInt32Length(Encoding.Latin1.GetBytes(v.Name));
		}
	}

	// SetNameIIR (0xADCE0CDA).
	public sealed record SetNameIIR(uint Handle, string Name, uint NameType = SetNames.NameTypeCustom, byte Flag = 1, byte Refresh = 0, byte IsToBePassed = 0);

	// The two semantic forms of a SetName. VALUE FACTORIES, not Build overloads (#530 §2 rule 1) - they take the same
	// (handle, name) pair and differ only in the nameplate-refresh bool, which is exactly the ambiguity an overload
	// would hide.
	public static class SetNames
	{
		// The nameType the captured records carry. 1..4 are the client's switch cases; 3 with `refresh` set is the one
		// that repaints an already-spawned entity's floating name.
		public const uint NameTypeCustom = 3;

		// The create-editor's "name accepted" record, echoing the chosen character name back so the client advances to
		// enter-world (dikketest idx 359, SendNicknameAccepted). No nameplate refresh - the avatar has not spawned yet.
		public static SetNameIIR Accepted(uint handle, string name)
		{
			return new SetNameIIR(handle, name);
		}

		// The in-WORLD nameplate override (#363): the same record with the refresh bool set, which is what makes an
		// already-spawned NPC show the name. The name is TRUNCATED to the client's cap rather than sent long, because
		// a longer one makes the client read length 0 and reject the whole blob.
		public static SetNameIIR Nameplate(uint handle, string? name)
		{
			var text = name ?? "";
			return new SetNameIIR(handle, text.Length > SetNameCodec.MaxNameLength
				? text[..SetNameCodec.MaxNameLength]
				: text, NameTypeCustom, 1, 1);
		}
	}
}
