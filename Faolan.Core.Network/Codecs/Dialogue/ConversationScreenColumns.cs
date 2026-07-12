using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Faolan.Core.Database.Tables;
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
				Antitag = s.Antitag == null ? null : uint.Parse(s.Antitag, CultureInfo.InvariantCulture)
			};
			for (var i = 0; i < s.Parameters.Count; i++)
				row.Parameters.Add(new ConversationScreenParameter { Sequence = i, Name = s.Parameters[i].Name, InnerRaw = s.Parameters[i].InnerRaw });

			// Verify the TYPED round-trip reproduces the captured screen byte-for-byte - so any lossy field (a
			// non-numeric / leading-zero id, a value that doesn't survive the uint round-trip) surfaces at import
			// instead of corrupting the wire at send.
			if (RemoteFormatXml.Build(ToScreen(row)) != screenXml)
				throw new InvalidOperationException($"Conversation screen is not byte-exact round-trippable from columns: {screenXml}");

			return row;
		}

		// Project a stored ConversationScreen row back to the RemoteFormatXml.Screen the builder serialises.
		public static RemoteFormatXml.Screen ToScreen(ConversationScreen row)
		{
			var pars = row.Parameters
				.OrderBy(p => p.Sequence)
				.Select(p => new RemoteFormatXml.Parameter(p.Name, p.InnerRaw))
				.ToList();

			return new RemoteFormatXml.Screen(
				row.ScreenId.ToString(CultureInfo.InvariantCulture),
				row.Category.ToString(CultureInfo.InvariantCulture),
				row.Key,
				row.Knubot.ToString(CultureInfo.InvariantCulture),
				row.Antitag?.ToString(CultureInfo.InvariantCulture),
				pars);
		}

		// The wire screen bytes (Latin1) for a stored row - the runtime's build-from-columns entry point.
		public static IReadOnlyList<RemoteFormatXml.Screen> ToScreens(IEnumerable<ConversationScreen> rows)
		{
			return rows.OrderBy(r => r.Sequence).Select(ToScreen).ToList();
		}
	}
}
