using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildInvite (0x202A) - pops the client's accept/decline dialog:
	//   [guild ID32][guildName s16][invitee ID32][inviterName s16]
	// Byte-exact against the captured Kyloth -> Hoxxling invite (GuildPacketTests). Moved in from
	// `GuildService.WriteGuildInviteBody` in #530 wave 7.
	public static class GuildInviteCodec
	{
		public const ushort Opcode = (ushort)CSPlayerInterfaceOpcodes.DispatchGuildInvite;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GuildInvite value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static GuildInvite Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildInviteCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GuildInvite Read(ref SpanReader r)
		{
			r.ExpectU32Be(EntityClass.Guild, "guild entity class");
			var guildId = r.ReadU32Be();
			var guildName = r.ReadStringU16Length();
			r.ExpectU32Be(EntityClass.Character, "invitee entity class");

			return new GuildInvite(guildId, guildName, r.ReadU32Be(), r.ReadStringU16Length());
		}

		internal static void Write(FaolanStream s, GuildInvite v)
		{
			s
				.WriteUInt32(EntityClass.Guild)
				.WriteUInt32(v.GuildId)
				.WriteString(v.GuildName)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.InviteeId)
				.WriteString(v.InviterName);
		}
	}

	// DispatchGuildInvite (0x202A).
	public sealed record GuildInvite(uint GuildId, string GuildName, uint InviteeId, string InviterName);
}
