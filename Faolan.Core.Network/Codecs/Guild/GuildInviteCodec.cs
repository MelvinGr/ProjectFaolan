using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildInvite</c> - pops the client's accept/decline dialog:
	///     <c>[guild ID32][guildName s16][invitee ID32][inviterName s16]</c>. Byte-exact against the captured
	///     Kyloth-to-Hoxxling invite (<c>GuildPacketTests</c>).
	/// </summary>
	public static class GuildInviteCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildInvite;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The DispatchGuildInvite body.</summary>
	public sealed record GuildInvite(uint GuildId, string GuildName, uint InviteeId, string InviterName);
}
