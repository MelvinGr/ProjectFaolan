using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Guild
{
	/// <summary>
	///     <c>DispatchGuildInviteNoSuchName</c> - "there is nobody called that": <c>[string name]</c>, the name the
	///     inviter typed, echoed so the client can put it in the message.
	/// </summary>
	/// <remarks>
	///     One string and nothing else - <c>CSPlayerInterfaceDispatch_t::DispatchGuildInviteNoSuchName</c>
	///     (FUN_01341490) reads the string primitive once and dispatches. No capture shows a failed guild invite. [V0]
	/// </remarks>
	public static class GuildInviteNoSuchNameCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchGuildInviteNoSuchName;

		public static byte[] Build(GuildInviteNoSuchName value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteString(value.Name);

			return s.ToArray();
		}

		public static byte[] Build(string name)
		{
			return Build(new GuildInviteNoSuchName(name));
		}

		public static GuildInviteNoSuchName Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildInviteNoSuchNameCodec));
			var value = new GuildInviteNoSuchName(r.ReadStringU16Length());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record GuildInviteNoSuchName(string Name);
}
