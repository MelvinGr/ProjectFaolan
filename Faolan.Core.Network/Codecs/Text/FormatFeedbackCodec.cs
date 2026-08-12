using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Text
{
	// Builds/parses the FormatFeedbackIIR (sub-opcode 0x8D5A8B6B) body - AoC's ON-SCREEN feedback / notification
	// mechanism (NOT chat). The client renders the carried `remoteformat` string as on-screen text; with the
	// `showfifomessagefromserver(...)` token family it drives the top-of-screen FIFO message banners (zone-name
	// reveal `GenericDisplayAreaName`, pickup notices `GenericSimpleDynelPickup`, PvP trackers, ...). Plain tokens
	// (`AttributeChanged`/`Increased`, `LostGraceEffect`, ...) render the smaller feedback lines. The CATALOG of
	// those strings is FeedbackXml next door; this codec only carries one, whatever it says.
	//
	// Carried as an IIR on the WORLD conversation (opcode 0x2000; sender = GameServerListener.WorldSenderEndpoint 0xB171CE13/0x58,
	// receiver = the player's GameInterface = GameInterfaceType + ClientInstance). The IIR body's `instance` equals
	// that receiver instance (verified in the Strie capture: all 1249 GameInterfaceType packets AND the feedback share
	// 0x02165EFF), so a live send is fully addressable from the session, not just a replay.
	//
	// Wire grammar (big-endian, decoded 2026-06-14 from the gameplay/Has-inventory captures, golden-tested in
	// FormatFeedbackCodecTests). The carrier's [u32 len] belongs to the SENDER (#530 rule 2) - WorldIir.Send writes it:
	//   [u32 0x8D5A8B6B]   FormatFeedbackIIR sub-opcode
	//   [u32 0x0000C350]   standard IIR marker
	//   [u32 instance]     the target GameInterface object (= the receiver endpoint instance / ClientInstance)
	//   [u8  0x01]         lead flag (constant)
	//   [u32 subjectClass][u32 subjectHandle]   the entity the message is ABOUT, or (0, 0) when it is about nobody
	//   [u32 typeFlags]    feedback type/priority (0x00000000 for the area-name FIFO banner; 0x42000020 etc. for
	//                      buff/attribute lines - kept verbatim, semantics loose)
	//   [u16 xmlLen][remoteformat XML]      the message (no null terminator)
	//   [u32 footer]       trailing flag (0, 1, 2 or 4 in the captures)
	//
	// ⚠️ The subject pair was read as `[u64 reserved]`, asserted zero, until #811. It IS zero on 427 of the 431
	// captured records - and the other 4 are all the SAME message, LDB 162:23 = "RESISTED", each naming a real
	// entity (`0xC350` + a handle) and each carrying footer 4. So the assert was a constant read off a corpus that
	// is quiet about this one form, and it refused every record of it. Same shape as #809's four.
	//
	// ⚠️ Those four also make `typeFlags` look like something other than flags: they carry 0x3F800000, 0x44937E78,
	// 0xBF676DBC and 0 - i.e. 1.0f, 1179.9f, -0.904f and 0.0f, which is not a flag word. It stays a bare `uint` here
	// because that is what round-trips; whether the slot is a per-message PARAMETER rather than a type is #811's
	// remaining question, and the 32.00003f / 2.0000014f readings of 0x42000020 / 0x40000006 are exactly the kind of
	// coincidence that would make guessing it wrong.
	public static class FormatFeedbackCodec
	{
		// typeFlags of the buff/effect feedback LINES (not the FIFO banner) - e.g. LostGraceEffect, AttributeChanged.
		public const uint EffectLineTypeFlags = 0x42000020;

		// typeFlags value of the GenericDisplayAreaName / FIFO-banner messages (the top-of-screen overlay).
		public const uint FifoBannerTypeFlags = 0x00000000;
		public const uint Opcode = (uint)IirTypes.FormatFeedbackIIR; // 0x8D5A8B6B
		private const byte LeadFlag = 0x01;

		public static byte[] Build(FormatFeedbackIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(uint instance, uint typeFlags, uint footer, byte[] xml)
		{
			return Build(new FormatFeedbackIIR(instance, typeFlags, footer, xml));
		}

		// The XML is Latin1 on the wire (FeedbackXml composes it as text), so a caller holding the string never has to
		// spell the encoding.
		public static byte[] Build(uint instance, uint typeFlags, uint footer, string xml)
		{
			return Build(new FormatFeedbackIIR(instance, typeFlags, footer, Encoding.Latin1.GetBytes(xml)));
		}

		// THE PAYLOAD FORM (#539). The parent composes the child: each `FeedbackPayload` knows its own typeFlags and
		// footer - which the SERVICE used to pass alongside the child's text, so `AttributeChangedCodec.TypeFlags` was
		// spelled at two call sites in `Faolan/` and the payload codecs read as standalone messages with no declared
		// identity.
		public static byte[] Build(uint instance, FeedbackPayload payload)
		{
			ArgumentNullException.ThrowIfNull(payload);

			return Build(instance, payload.TypeFlags, payload.Footer, Xml(payload));
		}

		// The child codec for each payload form. This is the one place that maps a feedback message to its text.
		private static string Xml(FeedbackPayload payload)
		{
			return payload switch
			{
				AreaNameBanner banner => AreaNameBannerCodec.Build(banner),
				RespawnPointDiscovered point => RespawnPointDiscoveredCodec.Build(point),
				AttributeChanged attribute => AttributeChangedCodec.Build(attribute),
				FeatRequired feat => FeatRequiredCodec.Build(feat),
				FeedbackLiteral literal => literal.Xml,
				_ => throw new ArgumentOutOfRangeException(nameof(payload), payload, "unknown FormatFeedbackIIR payload")
			};
		}

		public static FormatFeedbackIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FormatFeedbackCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static FormatFeedbackIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "the FormatFeedbackIIR sub-opcode");
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var instance = r.ReadU32Be();
			r.ExpectU8(LeadFlag, "the lead flag");
			var subjectClass = r.ReadU32Be();
			var subjectHandle = r.ReadU32Be();
			var typeFlags = r.ReadU32Be();
			var xml = r.ReadBytesU16Length().ToArray();

			return new FormatFeedbackIIR(instance, typeFlags, r.ReadU32Be(), xml, subjectClass, subjectHandle);
		}

		internal static void Write(FaolanStream s, FormatFeedbackIIR v)
		{
			s.WriteUInt32(Opcode)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(v.Instance)
				.WriteByte(LeadFlag)
				.WriteUInt32(v.SubjectClass)
				.WriteUInt32(v.SubjectHandle)
				.WriteUInt32(v.TypeFlags)
				.WriteUInt16(v.Xml.Length)
				.WriteArray(v.Xml)
				.WriteUInt32(v.Footer);
		}
	}

	// `SubjectClass`/`SubjectHandle` name the entity the message is ABOUT and are (0, 0) on every feedback form but
	// "RESISTED" - see the codec's note. They default so the four Build overloads, which none of them uses, stay as
	// they are.
	public sealed record FormatFeedbackIIR(uint Instance, uint TypeFlags, uint Footer, byte[] Xml, uint SubjectClass = 0, uint SubjectHandle = 0);

	// One feedback MESSAGE, as the thing FormatFeedbackCodec carries. Each form owns the `typeFlags`/`footer` retail
	// sends it with, because those are properties of the message and not of the sender - passing them at the call site
	// is how the same message came to be framed two different ways in two services (#539).
	//
	// `Parse` still returns the OPAQUE `FormatFeedbackIIR`: a captured body can carry any remoteformat, including ones
	// we have no payload type for, and the round-trip has to reproduce it verbatim.
	public abstract record FeedbackPayload
	{
		public abstract uint TypeFlags { get; }
		public abstract uint Footer { get; }
	}

	// A captured remoteformat with nothing substituted into it (FeedbackXml's two literals), or a plain string a
	// service already holds. It exists so a fieldless message needs no parameterless record of its own - the wave-2
	// ruling - while still travelling through the payload seam.
	public sealed record FeedbackLiteral(string Xml, uint TypeFlags = FormatFeedbackCodec.FifoBannerTypeFlags, uint Footer = 1) : FeedbackPayload
	{
		public override uint TypeFlags { get; } = TypeFlags;
		public override uint Footer { get; } = Footer;
	}
}
