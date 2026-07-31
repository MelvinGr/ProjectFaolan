using System;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Text;

namespace Faolan.Core.Network.Codecs.World
{
	// A world object's display slot (ProtoSimpleDynelIIR proto field 3) <-> its columns. The field is a STRING on the
	// wire, and every captured value is a serialized remoteformat inner element - an LDB reference by id or name
	// token, or a nested parameter-less remoteformat root. Storing it as columns (rather than the serialized text)
	// is what lets the object's identity key on WHICH STRING it shows instead of on a blob; see WorldObject.
	//
	// Lives here rather than on the entity because the grammar (InnerElement) is in Faolan.Core.Network, which
	// Faolan.Core.Database does not reference.
	// NOT a codec (#530 rule 1): this is a row<->value mapping over InnerElementCodec's grammar, which is #533's
	// subject. The codec is InnerElementCodec; what lives here is which COLUMNS hold that element.
	public static class WorldObjectDisplay
	{
		// The wire value, rebuilt from the columns. Null when the object has no display element - which is also the
		// "row is not lifted" signal WorldObjectCodec refuses to build from.
		public static string? ToWire(WorldObject row)
		{
			ArgumentNullException.ThrowIfNull(row);
			return row.DisplayKind is { } kind
				? InnerElementCodec.Build(new InnerElement(kind, row.DisplayId, row.DisplayToken, row.DisplayCategory, row.DisplayFlags ?? "", LdbKey.ToWire(row.DisplayKey), null, row.DisplayKnubot))
				: null;
		}

		// Lift a captured display string into the columns. Returns false - leaving the row UN-LIFTED - when the value
		// is not a shape the grammar covers or does not rebuild byte-for-byte. That is deliberate and matches how an
		// un-parseable geometry record is handled: the row falls back to the verbatim captured slice rather than
		// being half-stored. A wrong display string would be fed to the client as-is (the `key` is a client-validated
		// content hash), so "cannot reproduce it" must never become "store an approximation".
		public static bool TryApply(WorldObject row, string? displayName)
		{
			ArgumentNullException.ThrowIfNull(row);
			Clear(row);
			if (displayName == null)
				return false;

			// InnerElementCodec.Parse THROWS on a shape it cannot account for (#530 rule 4); here that is one of the
			// two "not lifted" outcomes rather than an error, so it is caught and reported as false.
			InnerElement element;
			try
			{
				element = InnerElementCodec.Parse(displayName);
			}
			catch (CodecParseException)
			{
				return false;
			}

			if (InnerElementCodec.Build(element) != displayName)
				return false;

			// StringEntry has no column here: no captured world object carries a literal (they are LDB references or
			// nested roots), and a literal would be session state rather than placement truth.
			if (element.Kind == InnerElementKind.StringEntry)
				return false;

			row.DisplayKind = element.Kind;
			row.DisplayId = element.Id;
			row.DisplayToken = element.Token;
			row.DisplayCategory = element.Category;
			row.DisplayFlags = element.Flags;
			row.DisplayKey = LdbKey.Decode(element.Key) ?? new();
			row.DisplayKnubot = element.Knubot;
			return true;
		}

		public static void Clear(WorldObject row)
		{
			ArgumentNullException.ThrowIfNull(row);
			row.DisplayKind = null;
			row.DisplayId = null;
			row.DisplayToken = null;
			row.DisplayCategory = null;
			row.DisplayFlags = null;
			row.DisplayKey = new();
			row.DisplayKnubot = null;
		}
	}
}
