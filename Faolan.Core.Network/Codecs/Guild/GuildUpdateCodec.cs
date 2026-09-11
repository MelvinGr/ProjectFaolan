using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     The <c>GuildUpdateIIR_c</c> body - sets the entity's in-world guild tag, the guild name under the player name:
	///     <c>[subop][ID32 entityClass][char][u8 env][u8 clear][string guildName]</c>.
	/// </summary>
	/// <remarks>
	///     The two bytes before the name are the carrier's own <c>env</c> byte and then a bool, not one zero u16: the
	///     client's <c>ReadSubClass</c> (FUN_004E1BD0) reads a single byte into a bool at <c>this+0x2c</c> and only then
	///     the std::string, and that split is the only one the four captured bodies fit (12 + 1 + 1 + 2 + nameLen).
	///     <para>
	///         <c>Clear</c> is named from the correlation rather than from the client, which just calls it a bool: it is 1
	///         on exactly the one record with an empty name and 0 on the three that carry one, and the empty-name record
	///         is followed 42 frames later by the same character's tag being set to a guild - retail clears the tag and
	///         then writes the new one. [V0 on the name; the field itself is byte-proven.]
	///     </para>
	///     <para>
	///         The listener owns the packet framing (WriteHeader with the world endpoints) and the live send; this builds
	///         and round-trips the body without a live client (<c>GuildPacketTests</c>).
	///     </para>
	/// </remarks>
	public static class GuildUpdateCodec
	{
		public const uint Opcode = (uint)IirTypes.GuildUpdateIIR;

		public static byte[] Build(GuildUpdateIIR value)
		{
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId, string guildName)
		{
			return Build(new GuildUpdateIIR(characterId, guildName));
		}

		// Strict inverse of Build: a mis-routed body (wrong sub-opcode, wrong entity class, trailing bytes) throws rather
		// than decoding into something plausible. Wire strings are Latin1; see FaolanStream.ReadString.
		public static GuildUpdateIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildUpdateCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildUpdateIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "the GuildUpdateIIR sub-opcode");
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var characterId = r.ReadU32Be();
			var env = r.ReadU8();
			var clear = r.ReadU8();

			return new GuildUpdateIIR(characterId, r.ReadStringU16Length(), clear != 0, env);
		}

		internal static void Write(FaolanStream s, GuildUpdateIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.CharacterId).WriteByte(v.Env).WriteByte(v.Clear ? (byte)0x01 : (byte)0x00).WriteString(v.GuildName);
		}
	}

	public sealed record GuildUpdateIIR(uint CharacterId, string GuildName, bool Clear = false, byte Env = 0);
}
