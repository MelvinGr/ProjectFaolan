using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     SetNameIIR - sets an entity's custom name. One grammar, recovered byte-exact from n3InfoItemRemote::Construct
	///     (FUN_005b9d30) and SetNameIIR_t::ReadSubClass (FUN_007ae1d0):
	///     <code>
	///     [u32 subop][u32 class][u32 handle][u8 isToBePassed]   Construct; isToBePassed must be 0 or 1
	///     [u32 nameType][u8 flag][u8 refresh]                   ReadSubClass; this+0x48 u32, this+0x4c/0x4e bools
	///     [u32 nameLen][ASCII name]                             nameLen is a u32, hard-capped at 0x20 by the client
	///     </code>
	/// </summary>
	/// <remarks>
	///     <c>nameType</c> (this+0x48) is applied by Activate through FUN_006ed100(nameType, this+0x4e). It must be 1 to 4
	///     - 0 hits the switch default and the name is silently dropped - and 3 or 4 with <c>refresh</c> set also refresh
	///     the live nameplate (FUN_006dbc00, FUN_00447ba0), an entity's OverheadText being built from the LDB hash at spawn
	///     and only re-reading the custom name on that refresh. So 3/1/1 is what makes an already-spawned NPC show the name.
	///     <para>
	///         The create-editor's "name accepted" record is the same grammar, differing only in that last bool. The u32
	///         carrier length belongs to the framing sender.
	///     </para>
	/// </remarks>
	public static class SetNameCodec
	{
		public const uint Opcode = (uint)IirTypes.SetNameIIR;

		/// <summary>
		///     The client reads nameLen as a u32 and hard-caps it at 0x20, so 1 to 31 characters; a longer name makes it read
		///     length 0, skip the name and reject the blob.
		/// </summary>
		public const int MaxNameLength = 31;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
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

	/// <summary>The SetNameIIR body.</summary>
	public sealed record SetNameIIR(uint Handle, string Name, uint NameType = SetNames.NameTypeCustom, byte Flag = 1, byte Refresh = 0, byte IsToBePassed = 0);

	/// <summary>
	///     The two semantic forms of a SetName, as value factories rather than Build overloads: they take the same
	///     (handle, name) pair and differ only in the nameplate-refresh bool, which an overload could not express.
	/// </summary>
	public static class SetNames
	{
		/// <summary>
		///     The nameType the captured records carry. 1 to 4 are the client's switch cases; 3 with <c>refresh</c> set is
		///     the one that repaints an already-spawned entity's floating name.
		/// </summary>
		public const uint NameTypeCustom = 3;

		/// <summary>
		///     The create-editor's "name accepted" record, echoing the chosen character name back so the client advances to
		///     enter-world (dikketest idx 359, SendNicknameAccepted). No nameplate refresh: the avatar has not spawned yet.
		/// </summary>
		public static SetNameIIR Accepted(uint handle, string name)
		{
			return new SetNameIIR(handle, name);
		}

		/// <summary>
		///     The in-world nameplate override: the same record with the refresh bool set, which is what makes an
		///     already-spawned NPC show the name. The name is truncated to the client's cap rather than sent long, because a
		///     longer one makes the client read length 0 and reject the whole blob.
		/// </summary>
		public static SetNameIIR Nameplate(uint handle, string? name)
		{
			var text = name ?? "";
			return new SetNameIIR(handle, text.Length > SetNameCodec.MaxNameLength
				? text[..SetNameCodec.MaxNameLength]
				: text, NameTypeCustom, 1, 1);
		}
	}
}
