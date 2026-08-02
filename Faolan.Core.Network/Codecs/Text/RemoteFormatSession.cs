using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;

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

		/// <summary>
		///     The <c>flags</c> token the client declines localized text against for a character of this
		///     <paramref name="sex" />: <c>"m"</c>, <c>"f"</c>, or empty when it is neither (#418).
		/// </summary>
		/// <remarks>
		///     RECOVERED FROM THE CLIENT, because the corpus cannot say: all 718 captured screens carry
		///     <c>flags="m"</c> - every capture's player was male - so <c>"f"</c> was a guess, and an unknown token
		///     could break the client's lookup rather than fall back. <c>FUN_011012a0</c> reads stat <c>0x3B</c> (Sex)
		///     and branches on exactly two values:
		///     <code>
		///     PUSH 0x3b ; CALL getStat
		///     SUB  EAX,0x2 ; JZ  -> PUSH 0x6d   ; sex 2 (Male)   -> 'm'
		///     DEC  EAX     ; JNZ -> skip        ; anything but 3 -> no token at all
		///                    PUSH 0x66          ; sex 3 (Female) -> 'f'
		///     CALL toupper ; SUB EAX,0x41 ; MOV EAX,[EAX*4 + 0x21a2ad0]   ; index into the token table
		///     </code>
		///     So the token really is the plain letter, the mapping is our <see cref="Sex" /> enum verbatim, and a
		///     third value is not "some other token" but NO token - which is why this returns empty rather than
		///     inventing one.
		/// </remarks>
		public static string GenderFlag(Sex sex)
		{
			return sex switch
			{
				Sex.Male => "m",
				Sex.Female => "f",
				_ => ""
			};
		}

		// Regenerate the session string-entry parameters (name = player, guildname = their guild, npcguildname = the
		// NPC's guild label) from live values. A slot carrying a <localized …> element instead - npcguildname's
		// "StrangerGuild" token variant, npcname, the grammar tokens - is content, not session state, and is kept.
		//
		// `playerSex` drives the "name" slot's flags (#418). The other two keep their CAPTURED flags: they decline
		// against the guild's name, not the player's, and nothing has decoded what governs them. Passing
		// Sex.None keeps the captured flags on "name" too, which is what the construct-vs-capture gates want - a
		// re-encode of a parsed screen has to come back byte-identical.
		public static RemoteFormatScreen WithLivePlayer(RemoteFormatScreen s, string playerName, string guildName, string npcGuildName,
			Sex playerSex = Sex.None)
		{
			ArgumentNullException.ThrowIfNull(s);
			var pars = new List<RemoteFormatParameter>(s.Parameters.Count);
			pars.AddRange(s.Parameters.Select(p => !IsStringEntry(p.InnerRaw)
				? p
				: p.Name switch
				{
					"name" => StringEntry("name", playerName, playerSex == Sex.None
						? FlagsOf(p.InnerRaw)
						: GenderFlag(playerSex)),
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
