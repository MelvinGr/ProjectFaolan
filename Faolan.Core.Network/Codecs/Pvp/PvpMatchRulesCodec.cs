using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	/// <summary>
	///     The <c>PvPMatchRules_c</c> element - a custom PvP match's ruleset. On its own it is the whole body of c2s
	///     <c>PvPMatchMakingCreateMatch</c>: the client's writer <c>FUN_00558880</c> streams exactly this element and
	///     nothing else. Composed, it is the rules block inside the <c>i_cMatch</c> payload
	///     (<see cref="PvpMatchCodec" />).
	/// </summary>
	/// <remarks>
	///     The layout is pinned three ways from the client (docs/re/minigame-massivepvp-messages.md: the s2c readers
	///     <c>FUN_01346ef0</c>/<c>FUN_013470d0</c>, the c2s writer <c>FUN_00558880</c> and the local copy
	///     <c>FUN_00547070</c> all agree):
	///     <code>
	///     [u32 matchType][u32 mapId][u32 C][u32 D][u32 E]
	///     [u16 len][password Latin1]
	///     [u32 timeLimitSeconds][u32 scoreLimit][u32 H]
	///     [u8 hasPassword][u8 silent]
	///     [u32 queueType]
	///     </code>
	///     <para>
	///         Eight of the twelve fields are named by the client's own consumers (the community-server debug view, the
	///         Flash <c>PvPMinigame</c> API, the TeamGUI match views). C, D, E and H have no consumer in this build -
	///         the one view that would name them, <c>PvPSearchResultView</c>, is dead code on both ends of the wire -
	///         so they are carried by position with the column-type reading recorded on each: C the number of teams
	///         (debug default 2), D and E the team-size minimum and maximum (debug defaults 3 and 6), H nothing found.
	///         [V0: the PvPMatchRules_c C/D/E/H columns]
	///     </para>
	///     <para>
	///         <c>silent</c> is the client copier's gate byte at +0x3D: when 0 the client announces the match with fifo
	///         text (YouCreatedMatch / YouJoinedMatch / PlayerJoined) and the enclosing <c>i_cMatch</c> carries two
	///         extra tail words; when 1 it stays quiet and the tail words are absent. Both effects hang off this one
	///         byte, in the wire reader and the copier alike, which is what makes it safe to round-trip.
	///     </para>
	/// </remarks>
	public static class PvpMatchRulesCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.PvPMatchMakingCreateMatch;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(PvpMatchRules value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static PvpMatchRules Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(PvpMatchRulesCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static PvpMatchRules Read(ref SpanReader r)
		{
			return new PvpMatchRules(
				MatchType: r.ReadU32Be(),
				MapId: r.ReadU32Be(),
				NumberOfTeams: r.ReadU32Be(),
				TeamSizeMin: r.ReadU32Be(),
				TeamSizeMax: r.ReadU32Be(),
				Password: r.ReadStringU16Length(),
				TimeLimitSeconds: r.ReadU32Be(),
				ScoreLimit: r.ReadU32Be(),
				FieldH: r.ReadU32Be(),
				HasPassword: r.ReadU8() != 0,
				Silent: r.ReadU8() != 0,
				QueueType: r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, PvpMatchRules v)
		{
			s.WriteUInt32(v.MatchType).WriteUInt32(v.MapId).WriteUInt32(v.NumberOfTeams).WriteUInt32(v.TeamSizeMin).WriteUInt32(v.TeamSizeMax)
				.WriteString(v.Password)
				.WriteUInt32(v.TimeLimitSeconds).WriteUInt32(v.ScoreLimit).WriteUInt32(v.FieldH)
				.WriteByte(v.HasPassword).WriteByte(v.Silent)
				.WriteUInt32(v.QueueType);
		}
	}

	/// <summary>A custom match's ruleset, field names per the client's consumers (see the codec header).</summary>
	/// <param name="MatchType">The LDB 802 objective id (4 = Conquest, 5 = Capture The Skull) - `PvPMinigame.GetMatchType`.</param>
	/// <param name="MapId">The minigame's playfield id - the debug view's Map column.</param>
	/// <param name="NumberOfTeams">Positional `C` - the debug command's default is 2. [V0]</param>
	/// <param name="TeamSizeMin">Positional `D` - the debug command's default is 3. [V0]</param>
	/// <param name="TeamSizeMax">Positional `E` - the debug command's default is 6. [V0]</param>
	/// <param name="Password">The match password; the debug command sets it together with <paramref name="HasPassword" />.</param>
	/// <param name="TimeLimitSeconds">`GetTimeLeft` computes off this; the debug default is 900.</param>
	/// <param name="ScoreLimit">`GetScoreLimit` returns this.</param>
	/// <param name="FieldH">Positional `H` - set by nothing found, read by nothing found. [V0]</param>
	/// <param name="HasPassword">The password gate the search view's Pwd column would have shown.</param>
	/// <param name="Silent">When false the client announces the match with fifo text and the i_cMatch tail carries two extra words.</param>
	/// <param name="QueueType">The debug view's QueueType column; 0 in every captured signup.</param>
	public sealed record PvpMatchRules(uint MatchType, uint MapId, uint NumberOfTeams, uint TeamSizeMin, uint TeamSizeMax, string Password, uint TimeLimitSeconds, uint ScoreLimit,
		uint FieldH, bool HasPassword, bool Silent, uint QueueType);
}
