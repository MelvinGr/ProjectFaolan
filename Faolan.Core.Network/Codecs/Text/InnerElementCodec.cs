using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// Byte-exact parse/build of a remoteformat INNER element - the value a `<parameter>` carries, and the value a
	// world object's display slot carries. These were the last serialized-XML blobs in the model
	// (ConversationScreenParameter.InnerRaw, WorldObject.DisplayName); this is what lets them be columns.
	//
	// A rule-1a STRING codec (#530): `string Build(InnerElement)` / `InnerElement Parse(string)`.
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
	public static class InnerElementCodec
	{
		private static readonly Regex LocalizedRx =
			new Regex("^<localized (?:token=\"(?<token>[^\"]*)\"|id=\"(?<id>\\d+)\") category=\"(?<cat>[^\"]*)\" flags=\"(?<flags>[^\"]*)\" key=\"(?<key>[^\"]*)\" >$", RegexOptions.Compiled);

		private static readonly Regex RemoteFormatRx =
			new Regex("^<remoteformat id=\"(?<id>\\d+)\" category=\"(?<cat>[^\"]*)\" key=\"(?<key>[^\"]*)\" knubot=\"(?<knubot>\\d+)\"  ></remoteformat>$", RegexOptions.Compiled);

		private static readonly Regex StringEntryRx = new Regex("^<stringentry string=\"(?<s>[^\"]*)\" flags=\"(?<flags>[^\"]*)\" >$", RegexOptions.Compiled);

		// Attribute ORDER and spacing are part of the bytes, not cosmetic - note the DOUBLE space before the
		// remoteformat's '>', which is its empty-antitag slot.
		public static string Build(InnerElement value)
		{
			ArgumentNullException.ThrowIfNull(value);
			switch (value.Kind)
			{
				case InnerElementKind.Localized:
					var reference = value.Token is { } token
						? $"token=\"{token}\""
						: $"id=\"{value.Id}\"";

					return $"<localized {reference} category=\"{value.Category}\" flags=\"{value.Flags}\" key=\"{value.Key}\" >";
				case InnerElementKind.StringEntry:
					return $"<stringentry string=\"{value.Text}\" flags=\"{value.Flags}\" >";
				case InnerElementKind.RemoteFormat:
					return $"<remoteformat id=\"{value.Id}\" category=\"{value.Category}\" key=\"{value.Key}\" knubot=\"{value.Knubot}\"  ></remoteformat>";
				default:
					throw new CodecParseException($"InnerElementCodec: unknown inner-element kind {value.Kind}.");
			}
		}

		// THROWS on anything that is not one of the three shapes (rule 4), so a caller REJECTS a value it cannot
		// rebuild rather than storing it. There is no blob column left to fall back on. It used to return null for
		// exactly that reason; the throw serves the same purpose and cannot be ignored by accident.
		public static InnerElement Parse(string text)
		{
			if (string.IsNullOrEmpty(text))
				throw new CodecParseException("InnerElementCodec: the inner element is empty.");

			var m = LocalizedRx.Match(text);
			if (m.Success)
			{
				return new InnerElement(InnerElementKind.Localized, m.Groups["id"].Success
					? uint.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture)
					: null, m.Groups["token"].Success
					? m.Groups["token"].Value
					: null, uint.Parse(m.Groups["cat"].Value, CultureInfo.InvariantCulture), m.Groups["flags"].Value, m.Groups["key"].Value, null, null);
			}

			m = StringEntryRx.Match(text);
			if (m.Success)
				return new InnerElement(InnerElementKind.StringEntry, null, null, null, m.Groups["flags"].Value, null, m.Groups["s"].Value, null);

			m = RemoteFormatRx.Match(text);
			if (m.Success)
			{
				return new InnerElement(InnerElementKind.RemoteFormat, uint.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), null,
					uint.Parse(m.Groups["cat"].Value, CultureInfo.InvariantCulture), "", m.Groups["key"].Value, null, uint.Parse(m.Groups["knubot"].Value, CultureInfo.InvariantCulture));
			}

			throw new CodecParseException($"InnerElementCodec: not a recognised inner element: {text}");
		}
	}

	public sealed record InnerElement(InnerElementKind Kind, uint? Id, string? Token, uint? Category, string Flags, string? Key, string? Text, uint? Knubot);
}
