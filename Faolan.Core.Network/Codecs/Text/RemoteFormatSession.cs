using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Faolan.Core.Network.Codecs.Text
{
	// LIVE-VALUE policy for a parsed remoteformat screen or bark, plus the XML escaping both codecs share. Not a
	// codec: nothing here encodes or decodes a message. It decides which of a screen's parameter slots hold SESSION
	// state (the player's name, their guild, the NPC's guild label) rather than dialogue content, and regenerates
	// them from live values on the way out - which is why those slots are never persisted (the capturing player's
	// character name, their guild, and the retail SERVER name would otherwise ride the DB into a live session).
	public static class RemoteFormatSession
	{
		private static readonly Regex FlagsRx = new Regex("flags=\"(?<f>.*?)\"", RegexOptions.Compiled);

		// The parameters whose value is SESSION state rather than dialogue content. ConversationScreenColumns blanks
		// them at import; WithLivePlayer fills them back in at send.
		public static readonly string[] SessionSlots = ["name", "guildname", "npcguildname"];

		public static string FlagsOf(string innerRaw)
		{
			var m = FlagsRx.Match(innerRaw);

			return m.Success
				? m.Groups["f"].Value
				: "";
		}

		// A bark slot whose value is the ADDRESSED PLAYER rather than content, so it is regenerated per send and its
		// captured value must never be stored (#417). Every such slot in the corpus names a character and ends in
		// `.name` - `DetectedChar.name` (the sendtell crier) and `Target.Name` (the Damsel) - while the one content
		// slot, `helptext`, does not. Checked case-insensitively because the two differ in case.
		public static bool IsSessionBarkSlot(string name)
		{
			ArgumentNullException.ThrowIfNull(name);

			return name.EndsWith(".name", StringComparison.OrdinalIgnoreCase);
		}

		public static bool IsStringEntry(string innerRaw)
		{
			ArgumentNullException.ThrowIfNull(innerRaw);

			return innerRaw.StartsWith("<stringentry", StringComparison.Ordinal);
		}

		// The NPC guild slot's literal, as retail composed it: "<guild> [<server>]", e.g. captured as
		// "Not in guild [Legacy of Cimmeria]". Only the GUILDLESS form is attested (no captured NPC is in a guild) and
		// ours never are either, so that is the only form we build - the realm name is ours, not the capture's.
		public static string NotInGuildLabel(string realmName)
		{
			return $"Not in guild [{realmName}]";
		}

		// Convenience for the live (session) parameters: a literal string entry the client renders verbatim.
		public static RemoteFormatParameter StringEntry(string name, string value, string flags = "")
		{
			return new RemoteFormatParameter(name, $"<stringentry string=\"{value}\" flags=\"{flags}\" >");
		}

		// Regenerate the session string-entry parameters (name = player, guildname = their guild, npcguildname = the
		// NPC's guild label) from live values, preserving each parameter's captured flags (the grammatical gender the
		// localized text declines against). A slot carrying a <localized …> element instead - npcguildname's
		// "StrangerGuild" token variant, npcname, the grammar tokens - is content, not session state, and is kept.
		public static RemoteFormatScreen WithLivePlayer(RemoteFormatScreen s, string playerName, string guildName, string npcGuildName)
		{
			ArgumentNullException.ThrowIfNull(s);
			var pars = new List<RemoteFormatParameter>(s.Parameters.Count);
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

		// XML escape/unescape for the parameter value's inner (& must be first on the way out, last on the way in).
		// Internal because both remoteformat codecs compose through it and nothing outside the wire layer should.
		internal static string Escape(string s)
		{
			return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
		}

		internal static string Unescape(string s)
		{
			return s.Replace("&quot;", "\"").Replace("&gt;", ">").Replace("&lt;", "<").Replace("&amp;", "&");
		}
	}
}
