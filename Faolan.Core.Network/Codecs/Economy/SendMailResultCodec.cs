using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchSendMailResult (s2c 0x2003) - the outcome of a mail op. It is a FAMILY: the same method carries two
	// bodies, told apart by what follows the success byte.
	//   MailOpResult   [u8 success][u8 error]                     - a mark/delete/cancel result (2 bytes)
	//   MailSendResult [u8 success][u16 len][remoteformat XML]     - the result of a player SendMail (capture F500)
	// A `success` of 0 is what lets the client show "couldn't send" (e.g. a bad recipient).
	//
	// Was two `TradepostResponses` senders that each wrote their own body (#530 wave 7); the sends stay in `Faolan/`.
	public static class SendMailResultCodec
	{
		public const uint Method = (uint)TradepostInterfaceMethods.DispatchSendMailResult;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SendMailResult value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static SendMailResult Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SendMailResultCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static SendMailResult Read(ref SpanReader r)
		{
			var success = r.ReadU8() != 0;

			// The discriminator is the body WIDTH: the op result is one more byte, the send result a length-prefixed
			// string. Nothing in the message says which, so there is no type word to read here.
			return r.Remaining == sizeof(byte)
				? new MailOpResult(success, r.ReadU8())
				: new MailSendResult(success, r.ReadStringU16Length());
		}

		internal static void Write(FaolanStream s, SendMailResult v)
		{
			s.WriteByte(Convert.ToByte(v.Success));
			switch (v)
			{
				case MailOpResult op:
					s.WriteByte(op.Error);
					break;
				case MailSendResult send:
					s.WriteString(send.Xml);
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(v), v, "unknown DispatchSendMailResult form");
			}
		}
	}

	// DispatchSendMailResult (0x2003) - the family base.
	public abstract record SendMailResult(bool Success);

	// The 2-byte form: a mark/delete/cancel outcome.
	public sealed record MailOpResult(bool Success, byte Error) : SendMailResult(Success);

	// The player-SendMail form, carrying a generic result remoteformat.
	public sealed record MailSendResult(bool Success, string Xml) : SendMailResult(Success);

	// The forms, as VALUE factories.
	public static class SendMailResults
	{
		// The captured DispatchSendMailResult for a player SendMail (F500): a generic result remoteformat, fixed
		// across sends (its key is a client-validated content hash, so it is carried verbatim).
		public const string CapturedSendXml = "<remoteformat id=\"0\" category=\"0\" key=\"C4O?^XE&#39;^VC4O?^XE&#39;^V\" knubot=\"0\"  ></remoteformat>";

		public static MailSendResult Send(bool success)
		{
			return new MailSendResult(success, CapturedSendXml);
		}
	}
}
