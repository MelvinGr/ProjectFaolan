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
	// A captured `remoteformat` screen XML <-> its columnized ConversationScreen row (+ parameter children) (#188).
	// The row is the persisted structured RemoteFormatScreen; the runtime rebuilds the wire XML from it. A rule-1a
	// STRING codec (#530) - the row↔XML mapper around the remoteformat layer - gated byte-exact in
	// ConversationConstructTests.
	public static class ConversationScreenCodec
	{
		public static string Build(ConversationScreen row)
		{
			return RemoteFormatScreenCodec.Build(ConversationScreenColumns.ToScreen(row));
		}

		// Parse a captured screen XML into its ConversationScreen row. THROWS if the parse does not round-trip
		// byte-for-byte, so a screen shape we cannot reproduce surfaces at import instead of being stored lossy
		// (a non-numeric / leading-zero id, a value that doesn't survive the uint round-trip). `Sequence` is the
		// row's position in its packet and is not in the XML - the caller stamps it.
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

	// The ConversationScreen row <-> the RemoteFormatScreen the codec serialises. A mapper, not an encoder
	// (#530 §2 rule 1 / #533).
	public static class ConversationScreenColumns
	{
		// The row as it is PERSISTED: parsed and gated exactly like Parse, then with the session slots blanked. The
		// captured `name` / `guildname` / `npcguildname` string entries carry the capturing player's character name,
		// their guild, and the retail SERVER they played on ("Not in guild [Legacy of Cimmeria]") - none of which is
		// dialogue content, and all of which the runtime regenerates from the live player + realm at send time
		// (RemoteFormatSession.WithLivePlayer). Storing them would leak real retail names into faolan.db and the
		// committed faolan.sql seed, and a stale one would reach a client whenever the live value is missing. The
		// slot itself stays (name, order and flags are part of the screen the client parses); only the string goes.
		// Blanked AFTER the round-trip gate, so a screen we cannot reproduce byte-exactly still fails at import.
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

		// The parameter's inner element, rebuilt from its columns (#188 follow-up: the InnerRaw blob is retired).
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

		// Lift a captured inner element into the parameter's columns, refusing anything the columns cannot hold: an
		// unknown shape (InnerElementCodec.Parse throws), one that does not rebuild byte-for-byte, or the
		// nested-remoteformat kind (which only world objects carry, and which therefore has no column here). A reject
		// fails the import - the blob that used to absorb these is gone, so silently storing something
		// unreproducible is no longer possible.
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
