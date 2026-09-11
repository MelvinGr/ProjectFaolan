using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchYouGotMailNotification(nMailId)</c> - the unsolicited "you have mail" push: <c>[u64 nMailId]</c>.
	/// </summary>
	/// <remarks>
	///     Eight bytes, and the first four are <c>0x20D82B63</c> on every captured frame - retail's item-id epoch
	///     (<c>FullCharacterRoots.ItemIdEpoch</c>) - so a mail id lives in the same 64-bit id space as an item instance id
	///     and is carried whole rather than as a pair. Only the low half varies across the four captured frames, which is
	///     the server's allocation counter.
	///     <para>
	///         It rides CSPlayerInterface rather than the tradepost interface: the router tier, which is why it reaches a
	///         player who is nowhere near a mailbox. That is the point of the message, since the mailbox itself is polled.
	///     </para>
	/// </remarks>
	public static class MailNotificationCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchYouGotMailNotification;

		public static byte[] Build(MailNotification value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(ulong mailId)
		{
			return Build(new MailNotification(mailId));
		}

		public static MailNotification Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MailNotificationCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static MailNotification Read(ref SpanReader r)
		{
			return new MailNotification(r.ReadU64Be());
		}

		internal static void Write(FaolanStream s, MailNotification v)
		{
			s.WriteUInt64(v.MailId);
		}
	}

	public sealed record MailNotification(ulong MailId);
}
