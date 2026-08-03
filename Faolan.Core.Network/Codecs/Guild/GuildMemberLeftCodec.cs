using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	// DispatchGuildMemberLeft (0x202E) - the roster fan-out when someone leaves or is kicked:
	// `[ID32 member][string memberName]`.
	//
	// ⚠️ It is NOT the mirror of DispatchGuildMemberJoined, which carries the whole member ELEMENT after the id
	// (GuildMemberJoinedCodec, byte-exact against the capture). The client's two readers disagree on purpose:
	// `CSPlayerInterfaceDispatch_t::DispatchGuildMemberJoined` (FUN_01340aa0) calls the element deserializer
	// (FUN_00566740) for its second field, while `…MemberLeft` (FUN_013408f0) calls the STRING primitive
	// (FUN_0048f840). Which makes sense - a departure only has to name the row to remove and the toast to print.
	// So the catalog's field name `i_cMemberName` is accurate here and misleading on the joined twin.
	//
	// ⚠️ No capture performs a guild departure. [V0]
	public static class GuildMemberLeftCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildMemberLeft;

		public static byte[] Build(GuildMemberLeft value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(value.CharacterId).WriteString(value.MemberName);

			return s.ToArray();
		}

		public static byte[] Build(uint characterId, string memberName)
		{
			return Build(new GuildMemberLeft(characterId, memberName));
		}

		public static GuildMemberLeft Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildMemberLeftCodec));
			r.ExpectU32Be(EntityClass.Character, "member entity class");
			var value = new GuildMemberLeft(r.ReadU32Be(), r.ReadStringU16Length());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildMemberLeft(uint CharacterId, string MemberName);
}
