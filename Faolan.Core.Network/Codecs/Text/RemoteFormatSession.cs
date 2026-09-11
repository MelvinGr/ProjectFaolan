using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Codecs.Text
{
	/// <summary>
	///     Live-value policy for a parsed remoteformat screen or bark, plus the XML escaping both codecs share.
	/// </summary>
	/// <remarks>
	///     Not a codec: nothing here encodes or decodes a message. It decides which of a screen's parameter slots hold
	///     session state - the player's name, their guild, the NPC's guild label - rather than dialogue content, and
	///     regenerates them from live values on the way out. Those slots must never be persisted, or the capturing
	///     player's character name, their guild and the retail server name ride the DB into a live session.
	/// </remarks>
	public static class RemoteFormatSession
	{
		private static readonly Regex FlagsRx = new Regex("flags=\"(?<f>.*?)\"", RegexOptions.Compiled);

		/// <summary>The parameters whose value is session state rather than dialogue content.</summary>
		/// <remarks>ConversationScreenColumns blanks them at import; <see cref="WithLivePlayer" /> fills them back in at send.</remarks>
		public static readonly string[] SessionSlots = ["name", "guildname", "npcguildname"];

		public static string FlagsOf(string innerRaw)
		{
			var m = FlagsRx.Match(innerRaw);

			return m.Success
				? m.Groups["f"].Value
				: "";
		}

		/// <summary>
		///     Whether a bark slot's value is the addressed player rather than content, so that it is regenerated per
		///     send and its captured value is never stored.
		/// </summary>
		/// <remarks>
		///     Every such slot in the corpus names a character and ends in `.name` - `DetectedChar.name` (the sendtell
		///     crier) and `Target.Name` (the Damsel) - while the one content slot, `helptext`, does not. Checked
		///     case-insensitively because those two differ in case.
		/// </remarks>
		public static bool IsSessionBarkSlot(string name)
		{
			ArgumentNullException.ThrowIfNull(name);

			// Both spellings name the same thing - the per-viewer entity the bark is about - and both must be matched:
			// template 21992's broadcast carries `Target.displayname`, which does not end in `.name`, so missing it files
			// the capturing player's own name as content and ships it in the seed.
			return name.EndsWith(".name", StringComparison.OrdinalIgnoreCase)
			       || name.EndsWith(".displayname", StringComparison.OrdinalIgnoreCase);
		}

		public static bool IsStringEntry(string innerRaw)
		{
			ArgumentNullException.ThrowIfNull(innerRaw);

			return innerRaw.StartsWith("<stringentry", StringComparison.Ordinal);
		}

		/// <summary>
		///     The NPC guild slot's literal, as retail composed it: "&lt;guild&gt; [&lt;server&gt;]", captured as
		///     "Not in guild [Legacy of Cimmeria]".
		/// </summary>
		/// <remarks>
		///     Only the guildless form is attested - no captured NPC is in a guild, and ours never are either - so that
		///     is the only form built here. The realm name is ours, not the capture's.
		/// </remarks>
		public static string NotInGuildLabel(string realmName)
		{
			return $"Not in guild [{realmName}]";
		}

		/// <summary>A literal string entry the client renders verbatim - the shape the live session parameters take.</summary>
		public static RemoteFormatParameter StringEntry(string name, string value, string flags = "")
		{
			return new RemoteFormatParameter(name, $"<stringentry string=\"{value}\" flags=\"{flags}\" >");
		}

		/// <summary>
		///     The <c>flags</c> token the client declines localized text against for a character of this
		///     <paramref name="sex" />: <c>"m"</c>, <c>"f"</c>, or empty when it is neither.
		/// </summary>
		/// <remarks>
		///     Recovered from the client, because the corpus cannot say: all 718 captured screens carry
		///     <c>flags="m"</c>. <c>FUN_011012a0</c> reads stat <c>0x3B</c> (Sex) and branches on exactly two values:
		///     <code>
		///     PUSH 0x3b ; CALL getStat
		///     SUB  EAX,0x2 ; JZ  -> PUSH 0x6d   ; sex 2 (Male)   -> 'm'
		///     DEC  EAX     ; JNZ -> skip        ; anything but 3 -> no token at all
		///                    PUSH 0x66          ; sex 3 (Female) -> 'f'
		///     CALL toupper ; SUB EAX,0x41 ; MOV EAX,[EAX*4 + 0x21a2ad0]   ; index into the token table
		///     </code>
		///     So the token is the plain letter, the mapping is the <see cref="Sex" /> enum verbatim, and a third value
		///     is not "some other token" but no token at all - which is why this returns empty rather than inventing one.
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

		/// <summary>
		///     Regenerates the session string-entry parameters (name = player, guildname = their guild, npcguildname =
		///     the NPC's guild label) from live values.
		/// </summary>
		/// <param name="playerSex">
		///     Drives the "name" slot's flags. <see cref="Sex.None" /> keeps the captured flags on "name" too, which is
		///     what the construct-versus-capture gates need: a re-encode of a parsed screen has to come back
		///     byte-identical.
		/// </param>
		/// <remarks>
		///     A slot carrying a localized element instead - npcguildname's "StrangerGuild" token variant, npcname, the
		///     grammar tokens - is content rather than session state and is kept. guildname and npcguildname keep their
		///     captured flags: they decline against the guild's name, not the player's, and nothing has decoded what
		///     governs them.
		/// </remarks>
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
