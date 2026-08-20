using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchYouGotMailNotification(nMailId) - CSPlayerInterface 0xAB. The unsolicited "you have mail" push (#871).
	//
	//   [u64 nMailId]
	//
	// Eight bytes, and the first four are `0x20D82B63` on every captured frame - retail's item-id EPOCH
	// (`FullCharacterRoots.ItemIdEpoch`), so a mail id lives in the same 64-bit id space as an item instance id and
	// is carried whole rather than as a pair. Only the low half varies across the four captured frames, which is the
	// server's allocation counter.
	//
	// ⚠️ It rides **CSPlayerInterface**, not the tradepost interface - the router tier, which is why it reaches a
	// player who is nowhere near a mailbox. That is the whole point of the message: the mailbox itself is polled, so
	// without this a player only learns about mail by opening one.
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
