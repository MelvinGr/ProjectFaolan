using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Raid
{
	// s2c DispatchYouJoinedRaid (CSPlayerInterface 0x93) - the WHOLE raid, the way DispatchYouJoinedTeam is the whole
	// team. The client calls the parameter `i_cMatch` and reads it with `FUN_005584c0`, which is the only caller of
	// that reader in the exe, so despite the name it is the raid record and nothing else (#669).
	//
	//   [ cTeam ]                                   the raid itself - id, leader, its own member list, its settings
	//   [ u32 (count+1)*0x3F1 ][ cTeam x count ]    the member TEAMS
	//   [ u32 (count+1)*0x3F1 ][ member-flags x n ] one [ID32][bool][bool] per character
	//   [ RaidMatchTail ]                           two ID32s, three words and a flag
	//
	// ⚠️ **No capture carries this message.** A sweep of all 120 dumps finds zero raid traffic - no captured session
	// ever formed a raid - so every byte here comes from the client's reader rather than from retail's sender. The
	// FRAMING is therefore as certain as static RE gets (a wrong shape desyncs the client's stream and it says so, by
	// name, in its own assert), while the MEANING of the tail is not established at all. See gaps.md.
	public static class RaidMatchCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchYouJoinedRaid;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(RaidMatch value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			TeamRosterCodec.Write(s, value.Raid);

			s.WriteUInt32(ListFrame.ForCount(value.Teams.Count));
			foreach (var team in value.Teams)
				TeamRosterCodec.Write(s, team);

			s.WriteUInt32(ListFrame.ForCount(value.MemberFlags.Count));
			foreach (var flags in value.MemberFlags)
			{
				EntityIdCodec.Write(s, flags.Character);
				s.WriteByte(flags.FlagA).WriteByte(flags.FlagB);
			}

			var tail = value.Tail;
			EntityIdCodec.Write(s, tail.Ref1);
			s.WriteUInt32(tail.Word1).WriteByte(tail.Flag);
			EntityIdCodec.Write(s, tail.Ref2);
			s.WriteUInt32(tail.Word2).WriteUInt32(tail.Word3).WriteUInt32(tail.Word4);

			return s.ToArray();
		}

		// The raid a service holds: its own roster plus the member teams. Everything the client reads past that is
		// unmodelled, so it goes out at the tail's defaults.
		public static byte[] Build(TeamRoster raid, IReadOnlyList<TeamRoster> teams)
		{
			return Build(new RaidMatch(raid, teams, [], RaidMatchTail.Empty));
		}

		public static RaidMatch Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RaidMatchCodec));
			var raid = TeamRosterCodec.Read(ref r);

			var teamCount = r.ReadListCount();
			var teams = new List<TeamRoster>(teamCount);
			for (var i = 0; i < teamCount; i++)
				teams.Add(TeamRosterCodec.Read(ref r));

			var flagCount = r.ReadListCount();
			var memberFlags = new List<RaidMemberFlags>(flagCount);
			for (var i = 0; i < flagCount; i++)
				memberFlags.Add(new RaidMemberFlags(EntityIdCodec.Read(ref r), r.ReadU8() != 0, r.ReadU8() != 0));

			var ref1 = EntityIdCodec.Read(ref r);
			var word1 = r.ReadU32Be();
			var flag = r.ReadU8() != 0;
			var ref2 = EntityIdCodec.Read(ref r);
			var tail = new RaidMatchTail(ref1, word1, flag, ref2, r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());

			r.EnsureAtEnd();

			return new RaidMatch(raid, teams, memberFlags, tail);
		}
	}

	// One character's two raid flags, keyed by their ID32. The client stores them in a map keyed by that id
	// (`FUN_0055c0a0` inserts each element into a container rather than a vector), which is what says the ID32 is a
	// key rather than a payload. What the two bools SELECT is not recoverable from the reader. [V0]
	public sealed record RaidMemberFlags(EntityId Character, bool FlagA, bool FlagB);

	// What the client reads after the two lists: an ID32, a word, a flag, a second ID32, then three more words. The
	// reader gives their ORDER and WIDTH and nothing else - it stores each straight into a field of the raid object
	// and the raid UI is what consumes them. Named by position rather than guessed at; a raid we build sends zeros,
	// which is the same "we model none of this" the empty flag list states. [V0]
	public sealed record RaidMatchTail(EntityId Ref1, uint Word1, bool Flag, EntityId Ref2, uint Word2, uint Word3, uint Word4)
	{
		public static RaidMatchTail Empty { get; } = new(new EntityId(0, 0), 0, false, new EntityId(0, 0), 0, 0, 0);
	}

	public sealed record RaidMatch(TeamRoster Raid, IReadOnlyList<TeamRoster> Teams, IReadOnlyList<RaidMemberFlags> MemberFlags, RaidMatchTail Tail);
}
