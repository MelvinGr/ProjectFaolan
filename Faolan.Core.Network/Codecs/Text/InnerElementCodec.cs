using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     Byte-exact parse and build of a remoteformat inner element - the value a <c>parameter</c> carries, and the
	///     value a world object's display slot carries. A string codec: <c>string Build(InnerElement)</c> /
	///     <c>InnerElement Parse(string)</c>.
	/// </summary>
	/// <remarks>
	///     The whole grammar, verified over all 8232 stored values, every one of which parses and rebuilds
	///     byte-identically:
	///     <code>
	///     &lt;localized token="T" category="C" flags="F" key="K" &gt;    an LDB reference by name token
	///     &lt;localized id="I" category="C" flags="F" key="K" &gt;       an LDB reference by numeric id
	///     &lt;stringentry string="S" flags="F" &gt;                      a literal the client renders verbatim
	///     &lt;remoteformat id="I" category="C" key="K" knubot="N"  &gt;&lt;/remoteformat&gt;  a nested parameter-less root
	///     </code>
	///     <c>flags</c> is the grammatical gender or declension the localized text is rendered against, and <c>key</c> is
	///     the harvested client-validated content hash. Both are carried as the raw attribute text the capture holds, XML
	///     entities included, because the client is fed these bytes verbatim and re-escaping is not round-trip safe. Same
	///     rule as <c>StockPhrase.FormatKey</c>.
	/// </remarks>
	public static class InnerElementCodec
	{
		private static readonly Regex LocalizedRx =
			new Regex("^<localized (?:token=\"(?<token>[^\"]*)\"|id=\"(?<id>\\d+)\") category=\"(?<cat>[^\"]*)\" flags=\"(?<flags>[^\"]*)\" key=\"(?<key>[^\"]*)\" >$", RegexOptions.Compiled);

		private static readonly Regex RemoteFormatRx =
			new Regex("^<remoteformat id=\"(?<id>\\d+)\" category=\"(?<cat>[^\"]*)\" key=\"(?<key>[^\"]*)\" knubot=\"(?<knubot>\\d+)\"  ></remoteformat>$", RegexOptions.Compiled);

		private static readonly Regex StringEntryRx = new Regex("^<stringentry string=\"(?<s>[^\"]*)\" flags=\"(?<flags>[^\"]*)\" >$", RegexOptions.Compiled);

		// Attribute order and spacing are part of the bytes rather than cosmetic - note the double space before the
		// remoteformat's closing bracket, which is its empty-antitag slot.
		public static string Build(InnerElement value)
		{
			ArgumentNullException.ThrowIfNull(value);
			switch (value.Kind)
			{
				case InnerElementKind.Localized:
				{
					var reference = value.Token is { } token
						? $"token=\"{token}\""
						: $"id=\"{value.Id}\"";

					return $"<localized {reference} category=\"{value.Category}\" flags=\"{value.Flags}\" key=\"{value.Key}\" >";
				}

				case InnerElementKind.StringEntry:
				{
					return $"<stringentry string=\"{value.Text}\" flags=\"{value.Flags}\" >";
				}

				case InnerElementKind.RemoteFormat:
				{
					return $"<remoteformat id=\"{value.Id}\" category=\"{value.Category}\" key=\"{value.Key}\" knubot=\"{value.Knubot}\"  ></remoteformat>";
				}

				default:
				{
					throw new CodecParseException($"InnerElementCodec: unknown inner-element kind {value.Kind}.");
				}
			}
		}

		// Throws on anything that is not one of these shapes, so a caller rejects a value it cannot rebuild rather than
		// storing it. There is no blob column to fall back on.
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
