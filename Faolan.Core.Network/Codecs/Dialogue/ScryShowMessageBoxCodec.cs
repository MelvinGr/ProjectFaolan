using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// The scry-script CONFIRMATION BOX - s2c DispatchScryShowMessageBox (0x205E). This is the "are you sure?" the
	// client raises for a zone transition, and it is the last step of LOCAL TRAVEL (#506): the player talks to the
	// travel NPC, picks a destination and pays, and this box confirms the trip before the teleport. The c2s answer
	// that echoes its context back is ScryMessageBoxAnswerCodec next door.
	//
	// Body (decoded from the three local-travel boxes in NecroL80_..._chat-annotated-4, plus the castle-transition ones):
	//   [u16 len][latin1 XML][u32 context ×6][u32 scriptThreadCtxId][u16 0][u64 ctxCookie]
	//
	// ⚠️ That `u16 0` between the thread id and the cookie is real, not padding: the tail is 38 bytes in every
	// captured box, and 24 + 4 + 8 only accounts for 36. Its value is 0 in all five. Leaving it out shifts the
	// cookie two bytes and the client's echo would no longer match what we stored.
	//
	// The XML is a single self-describing `<localized …>` open tag with no body and no closing tag - the client
	// resolves it against its own string table and renders the result:
	//
	//   <localized id="1090" category="520" flags="" key="%z&#36;n_zw5ComNiHcqoW4j" >
	//
	// ⚠️ `id` is the DESTINATION MAP ID, not a message id. All three travel boxes name the map the player is about to
	// travel to - 1020 Field of the Dead, 1090 Ymir's Pass, 1092 the Amphitheatre of Karutonia - and each is followed
	// by a redirect and a MapChange to exactly that map. Category 520 is the client's map-name category.
	//
	// The 24 bytes of context between the XML and the thread id are OPAQUE and passed through: word[2..4] are constant
	// across every captured travel box (0x71 / 0x0250B94C / 0x0A31C0D3) while word[0..1] vary per box, and nothing in
	// the corpus pins what they mean. They are kept as a caller-supplied blob rather than invented field names.
	//
	// ⚠️ The PRICE is not in this packet. Local travel costs money - the wallet drops by a round 50,000 the instant the
	// destination is picked in the NPC's conversation, ~40 s BEFORE this box appears - so the charge belongs to the
	// selection step and this box only confirms the trip. Do not try to read a cost out of these bytes.
	public static class ScryShowMessageBoxCodec
	{
		// The client's map-name string category, which is what makes `id` readable as a map id.
		public const uint MapNameCategory = 520;
		public const ushort Opcode = (ushort)GameCharInterfaceOpcodes.DispatchScryShowMessageBox;

		// How many opaque context words sit between the XML and the thread id.
		private const int ContextWords = 6;

		// The six context words every captured travel box carries. word[0..1] vary per box (both observed forms are in
		// the corpus and the client accepts either); the rest are invariant. Sent verbatim because nothing decodes them.
		public static readonly uint[] TravelContext = [0x0000006F, 0x00000070, 0x00000071, 0x0250B94C, 0x0A31C0D3, 0x00000000];

		public static byte[] Build(ScryShowMessageBox value)
		{
			ArgumentNullException.ThrowIfNull(value);
			if (value.Context.Count != ContextWords)
				throw new ArgumentException($"the scry message-box context is {ContextWords} words", nameof(value));

			// Latin-1: the XML carries the client's own escaped key, which is 7-bit but may hold `&#92;`-style escapes.
			var text = Encoding.Latin1.GetBytes(value.LocalizedXml);
			using var s = new FaolanStream();
			s.WriteUInt16((ushort)text.Length).WriteArray(text);
			foreach (var word in value.Context)
				s.WriteUInt32(word);

			s.WriteUInt32(value.ThreadCtxId).WriteUInt16(0).WriteUInt64(value.Cookie);

			return s.ToArray();
		}

		// The body for a destination MAP. `key` is the 20-character localized-format key the client's string table is
		// addressed by; `threadCtxId` / `cookie` are the correlation pair the answer echoes back.
		public static byte[] Build(uint destinationMapId, string key, uint threadCtxId, ulong cookie)
		{
			return Build(new ScryShowMessageBox($"<localized id=\"{destinationMapId}\" category=\"{MapNameCategory}\" flags=\"\" key=\"{key}\" >", TravelContext, threadCtxId, cookie));
		}

		public static ScryShowMessageBox Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ScryShowMessageBoxCodec));
			var xml = r.ReadStringU16Length();
			var context = new uint[ContextWords];
			for (var i = 0; i < ContextWords; i++)
				context[i] = r.ReadU32Be();

			var threadCtxId = r.ReadU32Be();
			r.ExpectU16Be(0, "the reserved word before the cookie");
			var value = new ScryShowMessageBox(xml, context, threadCtxId, r.ReadU64Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	public sealed record ScryShowMessageBox(string LocalizedXml, IReadOnlyList<uint> Context, uint ThreadCtxId, ulong Cookie);
}
