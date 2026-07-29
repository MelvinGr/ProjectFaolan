using System;
using System.Collections.Generic;
using System.Globalization;
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

		// The ambient-bark root: the same shape as RootRx but keyed by `token=`, whose value is structured
		// `sayinchat(N)|<channel>(K)|<Group>`. The channel is NOT always a numbered conversation - the corpus has
		// `conversation1`, `conversation2` and `sendtell` - so it is captured as a name. Anchored and fully specified
		// so anything that is NOT a bark fails to match rather than being half-read.
		//
		// The slots are matched SEPARATELY (ChatterParamRx) rather than inlined here: a bark was assumed to carry at
		// most one, and the Damsel in Distress line carries two (#493).
		private static readonly Regex ChatterRootRx = new Regex(
			"^<remoteformat token=\"sayinchat\\((?<say>\\d+)\\)\\|(?<channel>[A-Za-z][A-Za-z0-9]*)\\((?<count>\\d+)\\)\\|(?<group>[^\"|]*)\" category=\"(?<cat>[^\"]*)\" key=\"(?<key>[^\"]*)\" knubot=\"(?<knubot>[^\"]*)\"  >"
			+ "(?<params>(?:<parameter value=\"7;[^\"]*\" name=\"[^\"]*\" />)*)</remoteformat>$",
			RegexOptions.Compiled);

		private static readonly Regex ChatterParamRx = new Regex("<parameter value=\"7;(?<pvalue>[^\"]*)\" name=\"(?<pname>[^\"]*)\" />", RegexOptions.Compiled);

		// A bark slot whose value is the ADDRESSED PLAYER rather than content, so it is regenerated per send and its
		// captured value must never be stored (#417). Every such slot in the corpus names a character and ends in
		// `.name` - `DetectedChar.name` (the sendtell crier) and `Target.Name` (the Damsel) - while the one content
		// slot, `helptext`, does not. Checked case-insensitively because the two differ in case.
		public static bool IsSessionBarkSlot(string name)
		{
			return name.EndsWith(".name", StringComparison.OrdinalIgnoreCase);
		}

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

		// The ambient NPC bark payload: the same PARAMETER-LESS root as a stock phrase, but its first attribute is
		// `token=` rather than `id=`, and the token is structured - `sayinchat(N)|conversationM(K)|<Group>`. So an
		// NpcChatter row's columns rebuild the exact CharacterChat (0x0022) string the capture carries. Latin-1
		// because that is the wire's string encoding. Note the DOUBLE space before `>`: these barks carry no antitag,
		// which is the same empty-attribute slot Build() produces for a screen without one.
		// `detectedCharName` fills the row's single parameter slot when it has one (the sendtell crier's
		// `DetectedChar.name` - the player being addressed). Passing nothing renders the slot empty, which is what a
		// broadcast bark does today: the send path picks one line for everyone in range, so there is no single
		// player to name. Per-recipient rendering is open work tracked in Gitea.
		public static string BuildChatter(NpcChatter row, string detectedCharName = "")
		{
			ArgumentNullException.ThrowIfNull(row);
			// Written out rather than routed through Build(): only the first attribute differs, but patching `id=` to
			// `token=` afterwards would be a blind string replace over a payload that also carries a content hash.
			// The trailing DOUBLE space is Build()'s empty-antitag slot, which these barks always have.
			// A SESSION slot renders the live player; a CONTENT slot renders its stored value verbatim, escaped
			// exactly as captured (it is already-escaped XML - a `<localized>` element - so it is emitted as-is).
			var parameters = string.Concat(row
				.Parameters.OrderBy(x => x.Ordinal)
				.Select(x => IsSessionBarkSlot(x.Name)
					? $"<parameter value=\"7;{Escape(detectedCharName)}\" name=\"{x.Name}\" />"
					: $"<parameter value=\"7;{x.Value}\" name=\"{x.Name}\" />"));
			return $"<remoteformat token=\"{ChatterToken(row)}\" category=\"{row.Category}\" key=\"{row.FormatKey}\" knubot=\"{row.Knubot}\"  >{parameters}</remoteformat>";
		}

		public static byte[] BuildChatterBytes(NpcChatter row, string detectedCharName = "")
		{
			return Encoding.Latin1.GetBytes(BuildChatter(row, detectedCharName));
		}

		public static string ChatterToken(NpcChatter row)
		{
			ArgumentNullException.ThrowIfNull(row);
			return $"sayinchat({row.SayInChat})|{row.Channel}({row.ChannelCount})|{row.Group}";
		}

		// Lift a captured bark into its columns. Returns null when the string is not a chatter root at all, so the
		// importer can report it rather than storing something it cannot rebuild. The parameter's VALUE is
		// deliberately not carried over - it is the capturing player's name (see NpcChatter.ParameterName).
		public static NpcChatter? ParseChatter(string xml)
		{
			return ParseChatter(xml, out _);
		}

		// `parameterValue` is the captured slot's value, unescaped - NOT stored, but the importer needs it to prove
		// the columns rebuild the captured string byte-for-byte before blanking it (the same gate-then-blank split
		// as ConversationScreenColumns.ToRow / ToStoredRow).
		public static NpcChatter? ParseChatter(string xml, out string parameterValue)
		{
			parameterValue = "";
			var m = ChatterRootRx.Match(xml);
			if (!m.Success)
				return null;

			var slots = new List<NpcChatterParameter>();
			foreach (Match pm in ChatterParamRx.Matches(m.Groups["params"].Value))
			{
				var name = pm.Groups["pname"].Value;
				var raw = pm.Groups["pvalue"].Value;
				if (IsSessionBarkSlot(name))
					parameterValue = Unescape(raw); // the live value, for the importer's round-trip gate only

				slots.Add(new NpcChatterParameter
				{
					Ordinal = slots.Count,
					Name = name,
					Value = IsSessionBarkSlot(name)
						? null
						: raw
				});
			}

			var row = new NpcChatter
			{
				SayInChat = uint.Parse(m.Groups["say"].Value, CultureInfo.InvariantCulture),
				Channel = m.Groups["channel"].Value,
				ChannelCount = uint.Parse(m.Groups["count"].Value, CultureInfo.InvariantCulture),
				Group = m.Groups["group"].Value,
				Category = uint.Parse(m.Groups["cat"].Value, CultureInfo.InvariantCulture),
				FormatKey = m.Groups["key"].Value,
				Knubot = uint.Parse(m.Groups["knubot"].Value, CultureInfo.InvariantCulture),
			};
			foreach (var slot in slots)
				row.Parameters.Add(slot);

			return row;
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
