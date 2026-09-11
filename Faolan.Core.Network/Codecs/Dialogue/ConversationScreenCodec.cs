using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	/// <summary>
	///     A captured <c>remoteformat</c> screen XML and its columnized <c>ConversationScreen</c> row plus parameter
	///     children. The row is the persisted structured <c>RemoteFormatScreen</c>; the runtime rebuilds the wire XML from
	///     it. A string codec - the row-to-XML mapper around the remoteformat layer - gated byte-exact in
	///     <c>ConversationConstructTests</c>.
	/// </summary>
	public static class ConversationScreenCodec
	{
		public static string Build(ConversationScreen row)
		{
			return RemoteFormatScreenCodec.Build(ConversationScreenColumns.ToScreen(row));
		}

		/// <summary>
		///     Parses a captured screen XML into its <c>ConversationScreen</c> row.
		/// </summary>
		/// <remarks>
		///     Throws if the parse does not round-trip byte-for-byte, so a screen shape that cannot be reproduced surfaces at
		///     import instead of being stored lossily (a non-numeric or leading-zero id, a value that does not survive the
		///     uint round-trip). <c>Sequence</c> is the row's position in its packet and is not in the XML; the caller stamps
		///     it.
		/// </remarks>
		public static ConversationScreen Parse(string text)
		{
			var s = RemoteFormatScreenCodec.Parse(text);
			var row = new ConversationScreen
			{
				ScreenId = uint.Parse(s.Id, CultureInfo.InvariantCulture),
				Category = uint.Parse(s.Category, CultureInfo.InvariantCulture),
				Key = LdbKey.Decode(s.Key) ?? new ProtoDecryptionKey(),
				Knubot = uint.Parse(s.Knubot, CultureInfo.InvariantCulture),
				Antitag = s.Antitag == null
					? null
					: uint.Parse(s.Antitag, CultureInfo.InvariantCulture)
			};
			for (var i = 0; i < s.Parameters.Count; i++)
			{
				var parameter = new ConversationScreenParameter
				{
					Sequence = i,
					Name = s.Parameters[i].Name
				};
				ConversationScreenColumns.SetInner(parameter, s.Parameters[i].InnerRaw);
				row.Parameters.Add(parameter);
			}

			if (Build(row) != text)
				throw new CodecParseException($"ConversationScreenCodec: screen is not byte-exact round-trippable from columns: {text}");

			return row;
		}
	}

	/// <summary>
	///     The <c>ConversationScreen</c> row and the <c>RemoteFormatScreen</c> the codec serialises: a mapper, not an
	///     encoder.
	/// </summary>
	public static class ConversationScreenColumns
	{
		// The row as it is persisted: parsed and gated exactly like Parse, then with the session slots blanked. The
		// captured `name` / `guildname` / `npcguildname` string entries carry the capturing player's character name, their
		// guild, and the retail server they played on - none of which is dialogue content, and all of which the runtime
		// regenerates from the live player and realm at send time (RemoteFormatSession.WithLivePlayer). Storing them would
		// put real retail names into faolan.db and the committed faolan.sql seed, and a stale one would reach a client
		// whenever the live value is missing. The slot itself stays, since name, order and flags are part of the screen the
		// client parses; only the string goes. Blanked after the round-trip gate, so a screen that cannot be reproduced
		// byte-exactly still fails at import.
		public static ConversationScreen ToStoredRow(string screenXml, int sequence)
		{
			var row = ConversationScreenCodec.Parse(screenXml);
			row.Sequence = sequence;
			foreach (var p in row.Parameters)
			{
				// Flags is preserved because it drives the localized declension.
				if (RemoteFormatSession.SessionSlots.Contains(p.Name) && p.Kind == InnerElementKind.StringEntry)
					p.Text = "";
			}

			return row;
		}

		// The parameter's inner element, rebuilt from its columns.
		public static string InnerRawOf(ConversationScreenParameter p)
		{
			ArgumentNullException.ThrowIfNull(p);

			return InnerElementCodec.Build(new InnerElement(p.Kind, p.LocalizedId, p.LocalizedToken, p.Category, p.Flags, LdbKey.ToWire(p.ContentKey), p.Text, null));
		}

		public static RemoteFormatScreen ToScreen(ConversationScreen row)
		{
			ArgumentNullException.ThrowIfNull(row);
			var pars = row.Parameters.OrderBy(p => p.Sequence).Select(p => new RemoteFormatParameter(p.Name, InnerRawOf(p))).ToList();

			return new RemoteFormatScreen(row.ScreenId.ToString(CultureInfo.InvariantCulture), row.Category.ToString(CultureInfo.InvariantCulture), LdbKey.ToWire(row.Key),
				row.Knubot.ToString(CultureInfo.InvariantCulture), row.Antitag?.ToString(CultureInfo.InvariantCulture), pars);
		}

		public static IReadOnlyList<RemoteFormatScreen> ToScreens(IEnumerable<ConversationScreen> rows)
		{
			ArgumentNullException.ThrowIfNull(rows);

			return rows.OrderBy(r => r.Sequence).Select(ToScreen).ToList();
		}

		// Lift a captured inner element into the parameter's columns, refusing anything the columns cannot hold: an unknown
		// shape (InnerElementCodec.Parse throws), one that does not rebuild byte-for-byte, or the nested-remoteformat kind,
		// which only world objects carry and therefore has no column here. A reject fails the import rather than storing
		// something unreproducible.
		internal static void SetInner(ConversationScreenParameter row, string innerRaw)
		{
			var element = InnerElementCodec.Parse(innerRaw);
			if (InnerElementCodec.Build(element) != innerRaw)
				throw new CodecParseException($"ConversationScreenCodec: parameter value is not byte-exact round-trippable from columns: {innerRaw}");
			if (element.Kind == InnerElementKind.RemoteFormat)
				throw new CodecParseException($"ConversationScreenCodec: parameter carries a nested remoteformat, which has no column here: {innerRaw}");

			row.Kind = element.Kind;
			row.LocalizedId = element.Id;
			row.LocalizedToken = element.Token;
			row.Category = element.Category;
			row.Flags = element.Flags;
			row.ContentKey = LdbKey.Decode(element.Key) ?? new ProtoDecryptionKey();
			row.Text = element.Text;
		}
	}
}
