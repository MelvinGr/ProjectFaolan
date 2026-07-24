using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.Text
{
	// Byte-exact parse/build of a remoteformat INNER element - the value a `<parameter>` carries, and the value a
	// world object's display slot carries. These were the last serialized-XML blobs in the model
	// (ConversationScreenParameter.InnerRaw, WorldObject.DisplayName); this is what lets them be columns.
	//
	// The whole grammar, verified over ALL 8232 stored values (every one parses AND rebuilds byte-identically):
	//   <localized token="T" category="C" flags="F" key="K" >    - an LDB reference by NAME token
	//   <localized id="I" category="C" flags="F" key="K" >       - an LDB reference by numeric id
	//   <stringentry string="S" flags="F" >                      - a literal the client renders verbatim
	//   <remoteformat id="I" category="C" key="K" knubot="N"  ></remoteformat>  - a nested parameter-less root
	//
	// `flags` is the grammatical gender/declension the localized text is rendered against, and `key` is the
	// harvested client-validated content hash. Both are carried as the raw ATTRIBUTE TEXT the capture holds, XML
	// entities included (`&#39;`, `&quot;`, `&#36;`) - the client is fed these bytes verbatim and re-escaping is not
	// round-trip safe. Same rule as StockPhrase.FormatKey.
	public sealed record InnerElement(InnerElementKind Kind, uint? Id, string? Token, uint? Category, string Flags, string? Key, string? Text, uint? Knubot)
	{
		private static readonly Regex LocalizedRx =
			new Regex("^<localized (?:token=\"(?<token>[^\"]*)\"|id=\"(?<id>\\d+)\") category=\"(?<cat>[^\"]*)\" flags=\"(?<flags>[^\"]*)\" key=\"(?<key>[^\"]*)\" >$", RegexOptions.Compiled);

		private static readonly Regex RemoteFormatRx =
			new Regex("^<remoteformat id=\"(?<id>\\d+)\" category=\"(?<cat>[^\"]*)\" key=\"(?<key>[^\"]*)\" knubot=\"(?<knubot>\\d+)\"  ></remoteformat>$", RegexOptions.Compiled);

		private static readonly Regex StringEntryRx = new Regex("^<stringentry string=\"(?<s>[^\"]*)\" flags=\"(?<flags>[^\"]*)\" >$", RegexOptions.Compiled);

		// Returns null when the text is not one of the three shapes, so a caller REJECTS it rather than storing
		// something it cannot rebuild. There is no blob column left to fall back on.
		public static InnerElement? Parse(string? xml)
		{
			if (string.IsNullOrEmpty(xml))
				return null;

			var m = LocalizedRx.Match(xml);
			if (m.Success)
			{
				return new InnerElement(InnerElementKind.Localized, m.Groups["id"].Success
					? uint.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture)
					: null, m.Groups["token"].Success
					? m.Groups["token"].Value
					: null, uint.Parse(m.Groups["cat"].Value, CultureInfo.InvariantCulture), m.Groups["flags"].Value, m.Groups["key"].Value, null, null);
			}

			m = StringEntryRx.Match(xml);
			if (m.Success)
				return new InnerElement(InnerElementKind.StringEntry, null, null, null, m.Groups["flags"].Value, null, m.Groups["s"].Value, null);

			m = RemoteFormatRx.Match(xml);
			if (m.Success)
			{
				return new InnerElement(InnerElementKind.RemoteFormat, uint.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), null,
					uint.Parse(m.Groups["cat"].Value, CultureInfo.InvariantCulture), "", m.Groups["key"].Value, null, uint.Parse(m.Groups["knubot"].Value, CultureInfo.InvariantCulture));
			}

			return null;
		}

		// Attribute ORDER and spacing are part of the bytes, not cosmetic - note the DOUBLE space before the
		// remoteformat's '>', which is its empty-antitag slot.
		public string Build()
		{
			switch (Kind)
			{
				case InnerElementKind.Localized:
					var reference = Token is { } token
						? $"token=\"{token}\""
						: $"id=\"{Id}\"";
					return $"<localized {reference} category=\"{Category}\" flags=\"{Flags}\" key=\"{Key}\" >";
				case InnerElementKind.StringEntry:
					return $"<stringentry string=\"{Text}\" flags=\"{Flags}\" >";
				case InnerElementKind.RemoteFormat:
					return $"<remoteformat id=\"{Id}\" category=\"{Category}\" key=\"{Key}\" knubot=\"{Knubot}\"  ></remoteformat>";
				default:
					throw new InvalidOperationException($"Unknown inner-element kind {Kind}.");
			}
		}
	}
}
