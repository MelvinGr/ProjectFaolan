using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

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
	public static class RemoteFormatXml
	{
		private static readonly Regex FlagsRx = new Regex("flags=\"(?<f>.*?)\"", RegexOptions.Compiled);

		private static readonly Regex ParamRx = new Regex("<parameter value=\"7;(?<inner>.*?)\" name=\"(?<name>.*?)\" />", RegexOptions.Compiled);

		// Root attrs, then the `knubot="X" {antitag} >` slot - antitag sits between two spaces (empty when absent,
		// giving the double space `knubot="0"  >`), so the optional group must NOT eat the trailing space.
		private static readonly Regex RootRx = new Regex("^<remoteformat id=\"(?<id>.*?)\" category=\"(?<cat>.*?)\" key=\"(?<key>.*?)\" knubot=\"(?<knubot>.*?)\" (?:antitag=\"(?<antitag>.*?)\")? >",
			RegexOptions.Compiled);

		public static string Build(Screen s)
		{
			var sb = new StringBuilder();
			var antitag = string.IsNullOrEmpty(s.Antitag)
				? ""
				: $"antitag=\"{s.Antitag}\"";
			sb.Append($"<remoteformat id=\"{s.Id}\" category=\"{s.Category}\" key=\"{s.Key}\" knubot=\"{s.Knubot}\" {antitag} >");
			foreach (var p in s.Parameters)
				sb.Append($"<parameter value=\"7;{Escape(p.InnerRaw)}\" name=\"{p.Name}\" />");
			sb.Append("</remoteformat>");
			return sb.ToString();
		}

		public static Screen Parse(string xml)
		{
			var root = RootRx.Match(xml);
			var pars = new List<Parameter>();
			foreach (Match m in ParamRx.Matches(xml))
				pars.Add(new Parameter(m.Groups["name"].Value, Unescape(m.Groups["inner"].Value)));

			return new Screen(root.Groups["id"].Value, root.Groups["cat"].Value, root.Groups["key"].Value, root.Groups["knubot"].Value, root.Groups["antitag"].Success
				? root.Groups["antitag"].Value
				: null, pars);
		}

		// Convenience for the two live (session) parameters: a literal string entry the client renders verbatim.
		public static Parameter StringEntry(string name, string value, string flags = "")
		{
			return new Parameter(name, $"<stringentry string=\"{value}\" flags=\"{flags}\" >");
		}

		// Regenerate the session-specific string-entry parameters (name = player, guildname = guild) from live values,
		// preserving each parameter's captured flags. Other parameters (NPC name, localized tokens) are kept as-is.
		public static Screen WithLivePlayer(Screen s, string playerName, string guildName)
		{
			var pars = new List<Parameter>(s.Parameters.Count);
			pars.AddRange(s.Parameters.Select(p => p.Name switch
			{
				"name"      => StringEntry("name", playerName, FlagsOf(p.InnerRaw)),
				"guildname" => StringEntry("guildname", guildName, FlagsOf(p.InnerRaw)),
				_           => p
			}));

			return s with
			{
				Parameters = pars
			};
		}

		// XML escape/unescape for the parameter value's inner (& must be first on the way out, last on the way in).
		private static string Escape(string s)
		{
			return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
		}

		private static string FlagsOf(string innerRaw)
		{
			var m = FlagsRx.Match(innerRaw);
			return m.Success
				? m.Groups["f"].Value
				: "";
		}

		private static string Unescape(string s)
		{
			return s.Replace("&quot;", "\"").Replace("&gt;", ">").Replace("&lt;", "<").Replace("&amp;", "&");
		}

		public sealed record Parameter(string Name, string InnerRaw);

		public sealed record Screen(string Id, string Category, string Key, string Knubot, string? Antitag, IReadOnlyList<Parameter> Parameters);
	}
}
