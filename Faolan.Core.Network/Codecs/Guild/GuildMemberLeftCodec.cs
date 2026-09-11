using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildMemberLeft</c> - the roster fan-out when someone leaves or is kicked:
	///     <c>[ID32 member][string memberName]</c>.
	/// </summary>
	/// <remarks>
	///     It is not the mirror of <c>DispatchGuildMemberJoined</c>, which carries the whole member element after the id.
	///     The client's two readers differ deliberately:
	///     <c>CSPlayerInterfaceDispatch_t::DispatchGuildMemberJoined</c> (FUN_01340aa0) calls the element deserializer
	///     (FUN_00566740) for its second field, while <c>DispatchGuildMemberLeft</c> (FUN_013408f0) calls the string
	///     primitive (FUN_0048f840) - a departure only has to name the row to remove and the toast to print. The
	///     catalog's field name <c>i_cMemberName</c> is accurate here and misleading on the joined twin.
	///     <para>No capture performs a guild departure. [V0]</para>
	/// </remarks>
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
