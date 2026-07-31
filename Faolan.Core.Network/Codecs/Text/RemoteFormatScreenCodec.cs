using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// Byte-exact parse/build of a `remoteformat` dialogue screen (#188) so a Knubot screen can be GENERATED from
	// structured fields instead of replaying captured XML. A screen is:
	//   <remoteformat id="I" category="C" key="K" knubot="N" {antitag="A"} >( <parameter> )*</remoteformat>
	// each parameter:
	//   <parameter value="7;{xml-escaped inner element}" name="P" />
	// The inner element (a <localized .../> or <stringentry .../>) is carried as its once-escaped raw string; the
	// builder re-escapes it for the parameter value, so the model stays agnostic of the inner element shape. The
	// `key` is the client-validated content hash (harvested, not computable) and is carried verbatim as-escaped.
	//
	// A rule-1a STRING codec (#530). The session-slot substitution that runs over a parsed screen before it is sent
	// is RemoteFormatSession next door - that is live-value policy, not encoding.
	public static class RemoteFormatScreenCodec
	{
		private static readonly Regex ParamRx = new Regex("<parameter value=\"7;(?<inner>.*?)\" name=\"(?<name>.*?)\" />", RegexOptions.Compiled);

		// Root attrs, then the `knubot="X" {antitag} >` slot - antitag sits between two spaces (empty when absent,
		// giving the double space `knubot="0"  >`), so the optional group must NOT eat the trailing space.
		private static readonly Regex RootRx = new Regex("^<remoteformat id=\"(?<id>.*?)\" category=\"(?<cat>.*?)\" key=\"(?<key>.*?)\" knubot=\"(?<knubot>.*?)\" (?:antitag=\"(?<antitag>.*?)\")? >",
			RegexOptions.Compiled);

		public static string Build(RemoteFormatScreen value)
		{
			ArgumentNullException.ThrowIfNull(value);
			var sb = new StringBuilder();
			var antitag = string.IsNullOrEmpty(value.Antitag)
				? ""
				: $"antitag=\"{value.Antitag}\"";
			sb.Append($"<remoteformat id=\"{value.Id}\" category=\"{value.Category}\" key=\"{value.Key}\" knubot=\"{value.Knubot}\" {antitag} >");
			foreach (var p in value.Parameters)
				sb.Append($"<parameter value=\"7;{RemoteFormatSession.Escape(p.InnerRaw)}\" name=\"{p.Name}\" />");
			sb.Append("</remoteformat>");

			return sb.ToString();
		}

		public static RemoteFormatScreen Parse(string text)
		{
			var root = RootRx.Match(text);
			if (!root.Success)
				throw new CodecParseException($"RemoteFormatScreenCodec: not a remoteformat screen root: {text}");

			var pars = new List<RemoteFormatParameter>();
			foreach (Match m in ParamRx.Matches(text))
				pars.Add(new RemoteFormatParameter(m.Groups["name"].Value, RemoteFormatSession.Unescape(m.Groups["inner"].Value)));

			return new RemoteFormatScreen(root.Groups["id"].Value, root.Groups["cat"].Value, root.Groups["key"].Value, root.Groups["knubot"].Value, root.Groups["antitag"].Success
				? root.Groups["antitag"].Value
				: null, pars);
		}
	}

	public sealed record RemoteFormatParameter(string Name, string InnerRaw);

	// The screen forms our own senders build, as VALUE factories - row -> value mapping, not encoding (#533).
	public static class RemoteFormatScreens
	{
		// The cutscene stock-phrase payload (#410): every captured phrase is a PARAMETER-LESS root, so a StockPhrase
		// row's four attribute columns rebuild the exact text the 0x205D reply carries - the blob column they replaced
		// is gone. It used to be a `byte[] Build(StockPhrase)` overload on the codec, which rule 1a allows only while
		// the text IS the whole wire payload; since #530 wave 7 it is composed INTO ReceivedStockPhraseTextCodec, so
		// the string is what the caller wants and the Latin1 encoding belongs to that codec's `WriteString`.
		public static RemoteFormatScreen ForStockPhrase(StockPhrase row)
		{
			ArgumentNullException.ThrowIfNull(row);

			return new RemoteFormatScreen($"{row.RemoteFormatId}", $"{row.Category}", LdbKey.ToWire(row.FormatKey), $"{row.Knubot}", null, []);
		}
	}

	public sealed record RemoteFormatScreen(string Id, string Category, string Key, string Knubot, string? Antitag, IReadOnlyList<RemoteFormatParameter> Parameters);
}
