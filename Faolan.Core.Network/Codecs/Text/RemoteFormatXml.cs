using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Database.Tables;

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

		// The cutscene stock-phrase payload (#410): every captured phrase is a PARAMETER-LESS root, so a StockPhrase
		// row's four attribute columns rebuild the exact bytes the 0x205D reply carries - the blob column they
		// replaced is gone. Latin-1 because that is the wire's string encoding.
		public static byte[] BuildStockPhrase(StockPhrase row)
		{
			return Encoding.Latin1.GetBytes(Build(new Screen($"{row.RemoteFormatId}", $"{row.Category}", row.FormatKey, $"{row.Knubot}", null, [])));
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

		// The parameters whose value is SESSION state rather than dialogue content: the player's name, their guild, and
		// the NPC's guild label. They are regenerated from live values on every send (WithLivePlayer) and are NOT
		// persisted - ConversationScreenColumns.ToStoredRow blanks them so the capturing player's character/guild name
		// and their retail SERVER name never ride the DB into a live session.
		public static readonly string[] SessionSlots = ["name", "guildname", "npcguildname"];

		// Convenience for the live (session) parameters: a literal string entry the client renders verbatim.
		public static Parameter StringEntry(string name, string value, string flags = "")
		{
			return new Parameter(name, $"<stringentry string=\"{value}\" flags=\"{flags}\" >");
		}

		// The NPC guild slot's literal, as retail composed it: "<guild> [<server>]", e.g. captured as
		// "Not in guild [Legacy of Cimmeria]". Only the GUILDLESS form is attested (no captured NPC is in a guild) and
		// ours never are either, so that is the only form we build - the realm name is ours, not the capture's.
		public static string NotInGuildLabel(string realmName)
		{
			return $"Not in guild [{realmName}]";
		}

		// Regenerate the session string-entry parameters (name = player, guildname = their guild, npcguildname = the
		// NPC's guild label) from live values, preserving each parameter's captured flags (the grammatical gender the
		// localized text declines against). A slot carrying a <localized …> element instead - npcguildname's
		// "StrangerGuild" token variant, npcname, the grammar tokens - is content, not session state, and is kept.
		public static Screen WithLivePlayer(Screen s, string playerName, string guildName, string npcGuildName)
		{
			var pars = new List<Parameter>(s.Parameters.Count);
			pars.AddRange(s.Parameters.Select(p => !IsStringEntry(p.InnerRaw)
				? p
				: p.Name switch
				{
					"name"         => StringEntry("name", playerName, FlagsOf(p.InnerRaw)),
					"guildname"    => StringEntry("guildname", guildName, FlagsOf(p.InnerRaw)),
					"npcguildname" => StringEntry("npcguildname", npcGuildName, FlagsOf(p.InnerRaw)),
					_              => p
				}));

			return s with
			{
				Parameters = pars
			};
		}

		public static bool IsStringEntry(string innerRaw)
		{
			return innerRaw.StartsWith("<stringentry", StringComparison.Ordinal);
		}

		public static string FlagsOf(string innerRaw)
		{
			var m = FlagsRx.Match(innerRaw);
			return m.Success
				? m.Groups["f"].Value
				: "";
		}

		// XML escape/unescape for the parameter value's inner (& must be first on the way out, last on the way in).
		private static string Escape(string s)
		{
			return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
		}

		private static string Unescape(string s)
		{
			return s.Replace("&quot;", "\"").Replace("&gt;", ">").Replace("&lt;", "<").Replace("&amp;", "&");
		}

		public sealed record Parameter(string Name, string InnerRaw);

		public sealed record Screen(string Id, string Category, string Key, string Knubot, string? Antitag, IReadOnlyList<Parameter> Parameters);
	}
}
