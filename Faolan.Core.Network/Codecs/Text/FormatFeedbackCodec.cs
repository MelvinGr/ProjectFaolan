using System;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     The FormatFeedbackIIR body - the client's on-screen feedback and notification mechanism, not chat.
	/// </summary>
	/// <remarks>
	///     The client renders the carried <c>remoteformat</c> string as on-screen text; with the
	///     <c>showfifomessagefromserver(...)</c> token family it drives the top-of-screen FIFO message banners (the
	///     zone-name reveal <c>GenericDisplayAreaName</c>, pickup notices <c>GenericSimpleDynelPickup</c>, PvP trackers,
	///     …), while plain tokens (<c>AttributeChanged</c>/<c>Increased</c>, <c>LostGraceEffect</c>, …) render the
	///     smaller feedback lines. The catalog of those strings is <see cref="FeedbackXml" /> next door; this codec only
	///     carries one, whatever it says.
	///     <para>
	///         It is carried as an IIR on the world conversation (sender the world endpoint, receiver the player's
	///         GameInterface). The IIR body's <c>instance</c> equals that receiver instance - verified in the Strie
	///         capture, where all 1249 GameInterfaceType packets and the feedback share one instance - so a live send is
	///         fully addressable from the session rather than only replayable.
	///     </para>
	///     <para>
	///         Wire grammar, big-endian, decoded from the gameplay and Has-inventory captures and golden-tested in
	///         <c>FormatFeedbackCodecTests</c>. The carrier's <c>[u32 len]</c> belongs to the sender:
	///         <code>
	///         [u32 subop]        FormatFeedbackIIR sub-opcode
	///         [u32 entityClass]  standard IIR marker
	///         [u32 instance]     the target GameInterface object (the receiver endpoint instance / ClientInstance)
	///         [u8  0x01]         lead flag (constant)
	///         [u32 subjectClass][u32 subjectHandle]   the entity the message is about, or (0, 0) for nobody
	///         [u32 typeFlags]    feedback type/priority, kept verbatim, semantics loose
	///         [u16 xmlLen][remoteformat XML]          the message, with no null terminator
	///         [u32 footer]       trailing flag (0, 1, 2 or 4 in the captures)
	///         </code>
	///     </para>
	///     <para>
	///         The subject pair is zero on 427 of the 431 captured records, and the other 4 are all the same message,
	///         LDB 162:23 = "RESISTED", each naming a real entity and each carrying footer 4 - so it must not be asserted
	///         zero.
	///     </para>
	///     <para>
	///         Those four also make <c>typeFlags</c> look like something other than flags: they carry 0x3F800000,
	///         0x44937E78, 0xBF676DBC and 0 - 1.0f, 1179.9f, -0.904f and 0.0f - which is not a flag word. It stays a bare
	///         <c>uint</c> here because that is what round-trips; whether the slot is a per-message parameter rather than
	///         a type is undecided, and the 32.00003f / 2.0000014f readings of 0x42000020 / 0x40000006 are exactly the
	///         kind of coincidence that would make guessing it wrong.
	///     </para>
	/// </remarks>
	public static class FormatFeedbackCodec
	{
		// typeFlags of the buff/effect feedback lines (not the FIFO banner) - LostGraceEffect, AttributeChanged and the like.
		public const uint EffectLineTypeFlags = 0x42000020;

		// typeFlags of the GenericDisplayAreaName / FIFO-banner messages (the top-of-screen overlay).
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

		// The XML is Latin1 on the wire (FeedbackXml composes it as text), so a caller holding the string never has to spell
		// the encoding.
		public static byte[] Build(uint instance, uint typeFlags, uint footer, string xml)
		{
			return Build(new FormatFeedbackIIR(instance, typeFlags, footer, Encoding.Latin1.GetBytes(xml)));
		}

		// The payload form. The parent composes the child: each `FeedbackPayload` knows its own typeFlags and footer, so a
		// caller never spells them and one message cannot be framed two different ways in two services.
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
				DynamicAreaBanner banner => DynamicAreaBannerCodec.Build(banner),
				RespawnPointDiscovered point => RespawnPointDiscoveredCodec.Build(point),
				AttributeChanged attribute => AttributeChangedCodec.Build(attribute),
				FeatRequired feat => FeatRequiredCodec.Build(feat),
				AlternateAdvancementXpGranted aaXp => AlternateAdvancementXpCodec.Build(aaXp),
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

	/// <remarks>
	///     <c>SubjectClass</c> and <c>SubjectHandle</c> name the entity the message is about and are (0, 0) on every
	///     feedback form but "RESISTED" - see the codec's remarks. They default so the four <c>Build</c> overloads, none
	///     of which uses them, stay as they are.
	/// </remarks>
	public sealed record FormatFeedbackIIR(uint Instance, uint TypeFlags, uint Footer, byte[] Xml, uint SubjectClass = 0, uint SubjectHandle = 0);

	/// <summary>
	///     One feedback message, as the value <see cref="FormatFeedbackCodec" /> carries. Each form owns the
	///     <c>typeFlags</c> and <c>footer</c> retail sends it with, because those are properties of the message rather
	///     than of the sender.
	/// </summary>
	/// <remarks>
	///     <c>Parse</c> still returns the opaque <c>FormatFeedbackIIR</c>: a captured body can carry any remoteformat,
	///     including ones with no payload type here, and the round-trip has to reproduce it verbatim.
	/// </remarks>
	public abstract record FeedbackPayload
	{
		public abstract uint TypeFlags { get; }
		public abstract uint Footer { get; }
	}

	/// <summary>
	///     A captured remoteformat with nothing substituted into it (<see cref="FeedbackXml" />'s two literals), or a
	///     plain string a service already holds. It exists so a fieldless message needs no parameterless record of its
	///     own while still travelling through the payload seam.
	/// </summary>
	public sealed record FeedbackLiteral(string Xml, uint TypeFlags = FormatFeedbackCodec.FifoBannerTypeFlags, uint Footer = 1) : FeedbackPayload
	{
		public override uint TypeFlags { get; } = TypeFlags;
		public override uint Footer { get; } = Footer;
	}
}
