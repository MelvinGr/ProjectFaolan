using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildMemberJoined (0x202F) - [member ID32][member record]. Retail fans it out to every online guild
	// member, the joiner included (right after their own 0x203F).
	//
	// Moved in from `GuildService.SendGuildMemberJoined` in #530 wave 7.
	public static class GuildMemberJoinedCodec
	{
		public const ushort Opcode = (ushort)CSPlayerInterfaceOpcodes.DispatchGuildMemberJoined;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GuildMemberJoined value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		// The common form: the record's own character id is the ID32 the message is keyed by.
		public static byte[] Build(GuildMemberRecord member)
		{
			ArgumentNullException.ThrowIfNull(member);

			return Build(new GuildMemberJoined(member.CharacterId, member));
		}

		public static GuildMemberJoined Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildMemberJoinedCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildMemberJoined Read(ref SpanReader r)
		{
			r.ExpectU32Be(EntityClass.Character, "member entity class");

			return new GuildMemberJoined(r.ReadU32Be(), GuildMemberRecordCodec.Read(ref r));
		}

		internal static void Write(FaolanStream s, GuildMemberJoined v)
		{
			s.WriteUInt32(EntityClass.Character).WriteUInt32(v.CharacterId);
			GuildMemberRecordCodec.Write(s, v.Member);
		}
	}

	// DispatchGuildMemberJoined (0x202F).
	public sealed record GuildMemberJoined(uint CharacterId, GuildMemberRecord Member);
}
