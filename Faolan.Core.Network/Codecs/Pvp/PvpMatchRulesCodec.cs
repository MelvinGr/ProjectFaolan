using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Pvp
{
	// The `PvPMatchRules_c` element - a custom PvP match's ruleset. On its own it is the WHOLE body of c2s
	// PvPMatchMakingCreateMatch (0x204A): the client's writer `FUN_00558880` streams exactly this element and
	// nothing else, so what a create-request carries is what our Parse reads. Composed, it is the rules block
	// inside the `i_cMatch` payload (PvpMatchCodec).
	//
	// Layout, pinned three ways from the client (docs/re/minigame-massivepvp-messages.md: the s2c readers
	// `FUN_01346ef0`/`FUN_013470d0`, the c2s writer `FUN_00558880`, and the local copy `FUN_00547070` agree):
	//
	//   [u32 matchType][u32 mapId][u32 C][u32 D][u32 E]
	//   [u16 len][password LATIN1]
	//   [u32 timeLimitSeconds][u32 scoreLimit][u32 H]
	//   [u8 hasPassword][u8 silent]
	//   [u32 queueType]
	//
	// Eight of the twelve fields are NAMED by the client's own consumers (the community-server debug view, the
	// Flash `PvPMinigame` API, the TeamGUI match views). `C`/`D`/`E`/`H` have no consumer in this build - the one
	// view that would name them (`PvPSearchResultView`) is dead code on both ends of the wire (Gitea #894) - so
	// they are carried by POSITION with the column-type reading recorded on each: C the number of teams (debug
	// default 2), D/E the team-size min/max (debug defaults 3/6), H nothing found.
	// [V0: the PvPMatchRules_c C/D/E/H columns]
	//
	// `silent` (the client copier's gate byte at +0x3D): when 0 the client announces the match with fifo text
	// (YouCreatedMatch / YouJoinedMatch / PlayerJoined) AND the enclosing i_cMatch carries two extra tail words;
	// when 1 it stays quiet and the tail words are absent. Both effects hang off this ONE byte, in the wire
	// reader and the copier alike - which is what makes it safe to round-trip.
	public static class PvpMatchRulesCodec
	{
		public const uint Method = (uint)CSPlayerAgentMethods.PvPMatchMakingCreateMatch;

		// CANONICAL - the only method here that writes bytes.
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
