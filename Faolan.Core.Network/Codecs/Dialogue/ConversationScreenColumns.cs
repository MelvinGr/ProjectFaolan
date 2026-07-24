using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// Maps a captured `remoteformat` screen XML <-> its columnized ConversationScreen row (+ parameter children)
	// (#188). The row is the persisted structured RemoteFormatXml.Screen; the runtime rebuilds the wire XML from it
	// via RemoteFormatXml.Build. ToRow / ToScreen are inverses, gated byte-exact in ConversationConstructTests.
	public static class ConversationScreenColumns
	{
		// Parse a captured screen XML into its ConversationScreen row. Throws if the parse does not round-trip
		// byte-for-byte, so a screen shape we cannot reproduce surfaces at import instead of being stored lossy.
		public static ConversationScreen ToRow(string screenXml, int sequence)
		{
			var s = RemoteFormatXml.Parse(screenXml);
			var row = new ConversationScreen
			{
				Sequence = sequence,
				ScreenId = uint.Parse(s.Id, CultureInfo.InvariantCulture),
				Category = uint.Parse(s.Category, CultureInfo.InvariantCulture),
				Key = s.Key,
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
				SetInner(parameter, s.Parameters[i].InnerRaw);
				row.Parameters.Add(parameter);
			}

			// Verify the TYPED round-trip reproduces the captured screen byte-for-byte - so any lossy field (a
			// non-numeric / leading-zero id, a value that doesn't survive the uint round-trip) surfaces at import
			// instead of corrupting the wire at send.
			if (RemoteFormatXml.Build(ToScreen(row)) != screenXml)
				throw new InvalidOperationException($"Conversation screen is not byte-exact round-trippable from columns: {screenXml}");

			return row;
		}

		// The row as it is PERSISTED: parsed and gated exactly like ToRow, then with the session slots blanked. The
		// captured `name` / `guildname` / `npcguildname` string entries carry the capturing player's character name,
		// their guild, and the retail SERVER they played on ("Not in guild [Legacy of Cimmeria]") - none of which is
		// dialogue content, and all of which the runtime regenerates from the live player + realm at send time
		// (RemoteFormatXml.WithLivePlayer). Storing them would leak real retail names into faolan.db and the committed
		// faolan.sql seed, and a stale one would reach a client whenever the live value is missing. The slot itself
		// stays (name, order and flags are part of the screen the client parses); only the string goes.
		// Blanked AFTER ToRow's round-trip gate, so a screen we cannot reproduce byte-exactly still fails at import.
		public static ConversationScreen ToStoredRow(string screenXml, int sequence)
		{
			var row = ToRow(screenXml, sequence);
			foreach (var p in row.Parameters)
			{
				// The slot itself stays (name, order and flags are part of the screen the client parses); only the
				// literal goes. Flags is preserved because it drives the localized declension.
				if (RemoteFormatXml.SessionSlots.Contains(p.Name) && p.Kind == InnerElementKind.StringEntry)
					p.Text = "";
			}

			return row;
		}

		// Project a stored ConversationScreen row back to the RemoteFormatXml.Screen the builder serialises.
		// The parameter's inner element, rebuilt from its columns (#188 follow-up: the InnerRaw blob is retired).
		public static string InnerRawOf(ConversationScreenParameter p)
		{
			ArgumentNullException.ThrowIfNull(p);
			return new InnerElement(p.Kind, p.LocalizedId, p.LocalizedToken, p.Category, p.Flags, p.ContentKey, p.Text, null).Build();
		}

		// Lift a captured inner element into the parameter's columns, refusing anything the columns cannot hold: an
		// unknown shape, one that does not rebuild byte-for-byte, or the nested-remoteformat kind (which only world
		// objects carry, and which therefore has no column here). A reject fails the import - the blob that used to
		// absorb these is gone, so silently storing something unreproducible is no longer possible.
		private static void SetInner(ConversationScreenParameter row, string innerRaw)
		{
			if (InnerElement.Parse(innerRaw) is not { } element)
				throw new InvalidOperationException($"Conversation parameter value is not a recognised inner element: {innerRaw}");
			if (element.Build() != innerRaw)
				throw new InvalidOperationException($"Conversation parameter value is not byte-exact round-trippable from columns: {innerRaw}");
			if (element.Kind == InnerElementKind.RemoteFormat)
				throw new InvalidOperationException($"Conversation parameter carries a nested remoteformat, which has no column here: {innerRaw}");

			row.Kind = element.Kind;
			row.LocalizedId = element.Id;
			row.LocalizedToken = element.Token;
			row.Category = element.Category;
			row.Flags = element.Flags;
			row.ContentKey = element.Key;
			row.Text = element.Text;
		}

		public static RemoteFormatXml.Screen ToScreen(ConversationScreen row)
		{
			var pars = row.Parameters.OrderBy(p => p.Sequence).Select(p => new RemoteFormatXml.Parameter(p.Name, InnerRawOf(p))).ToList();

			return new RemoteFormatXml.Screen(row.ScreenId.ToString(CultureInfo.InvariantCulture), row.Category.ToString(CultureInfo.InvariantCulture), row.Key,
				row.Knubot.ToString(CultureInfo.InvariantCulture), row.Antitag?.ToString(CultureInfo.InvariantCulture), pars);
		}

		// The wire screen bytes (Latin1) for a stored row - the runtime's build-from-columns entry point.
		public static IReadOnlyList<RemoteFormatXml.Screen> ToScreens(IEnumerable<ConversationScreen> rows)
		{
			return rows.OrderBy(r => r.Sequence).Select(ToScreen).ToList();
		}
	}
}
