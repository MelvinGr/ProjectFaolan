using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// The scry-script CONFIRMATION BOX - s2c DispatchScryShowMessageBox (0x205E). The c2s answer that echoes its
	// context back is ScryMessageBoxAnswerCodec next door.
	//
	// ⚠️ It belongs to the ZONE-LINE DOOR, not to LOCAL TRAVEL (#514/#637). It was implemented as local travel's
	// confirmation step, which is wrong twice over: the wagoner trip teleports straight off the destination pick with
	// no box at all, and every captured box follows the player selecting a DOOR (the box at t=810.4 in
	// NecroL80_..._chat-annotated-4 follows door 0x863 at t=809.8, and the answer drives the redirect at t=812.9).
	//
	// The box is scoped to the DESTINATION MAP: across the corpus's 14 travel boxes and 17 harvested door
	// walk-throughs, doors to the same destination agree and carry the same key, and the confirming destinations are
	// a subset of the reachable ones - so which maps confirm is harvested onto Map.TravelConfirmKey rather than
	// inferred from "the door crosses a map".
	//
	// Body (decoded from the three zone-line boxes in NecroL80_..._chat-annotated-4, then re-checked against all 15 in
	// the corpus):
	//   [u16 len][latin1 XML][u32 context ×6][u32 scriptThreadCtxId][u16 0][u64 ctxCookie]
	//
	// ⚠️ That `u16 0` between the thread id and the cookie is real, not padding: the tail is 38 bytes in every
	// captured box, and 24 + 4 + 8 only accounts for 36. Its value is 0 in all fifteen. Leaving it out shifts the
	// cookie two bytes and the client's echo would no longer match what we stored.
	//
	// The XML is a single self-describing `<localized …>` open tag with no body and no closing tag - the client
	// resolves it against its own string table and renders the result:
	//
	//   <localized id="1090" category="520" flags="" key="%z&#36;n_zw5ComNiHcqoW4j" >
	//
	// ⚠️ `id` is the DESTINATION MAP ID, not a message id, on every travel box: all 14 name the map the player is about
	// to travel to - 1004 Spider Caverns, 1020 Field of the Dead, 1090 Ymir's Pass, 1092 the Amphitheatre of
	// Karutonia, 2010 Tarantia Noble District, 2090 Thunder River, 2094 - and each is followed by a redirect and a
	// MapChange to exactly that map. Category 520 is the client's map-name category.
	//
	// ⚠️ Category 520 is also what SEPARATES a travel box from the rest. This is a general script dialog: the corpus's
	// fifteenth box is category 10035 with an all-FF context and has nothing to do with travel, and it sits inside a
	// door use's lookahead window. Anything reading these off the wire must gate on the category (see
	// WorldTeleportExtractor) or it harvests that one as a door's confirmation.
	//
	// The 24 bytes of context between the XML and the thread id are OPAQUE and passed through: word[2..4] are constant
	// across every captured travel box (0x71 / 0x0250B94C / 0x0A31C0D3) while word[0..1] vary per box (0x6F/0x70 on
	// twelve of the fourteen, a pointer-looking pair on the other two), and nothing in the corpus pins what they mean.
	// They are kept as a caller-supplied blob rather than invented field names.
	//
	// ⚠️ The PRICE is not in this packet. Local travel costs money - the wallet drops by a round 50,000 the instant the
	// destination is picked in the NPC's conversation, ~40 s BEFORE this box appears - so the charge belongs to the
	// selection step and this box only confirms the trip. Do not try to read a cost out of these bytes.
	public static class ScryShowMessageBoxCodec
	{
		// The client's map-name string category, which is what makes `id` readable as a map id.
		public const uint MapNameCategory = 520;
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchScryShowMessageBox;

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
			var localized = new InnerElement(InnerElementKind.Localized, destinationMapId, null, MapNameCategory, "", key, null, null);
			return Build(new ScryShowMessageBox(InnerElementCodec.Build(localized), TravelContext, threadCtxId, cookie));
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
