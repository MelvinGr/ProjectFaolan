using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Team;

namespace Faolan.Core.Network.Codecs.Raid
{
	/// <summary>
	///     s2c <c>DispatchYouJoinedRaid</c> - the whole raid, the way <c>DispatchYouJoinedTeam</c> is the whole team.
	/// </summary>
	/// <remarks>
	///     The client calls the parameter <c>i_cMatch</c> and reads it with <c>FUN_005584c0</c>, the only caller of that
	///     reader in the exe, so despite the name it is the raid record and nothing else. The layout:
	///     <code>
	///     [ cTeam ]                          the raid itself - id, leader, its own member list, its settings
	///     [ list frame ][ cTeam x count ]    the member teams
	///     [ list frame ][ flags x count ]    one [ID32][bool][bool] per character
	///     [ RaidMatchTail ]                  two ID32s, three words and a flag
	///     </code>
	///     <para>
	///         No capture carries this message: a sweep of all 120 dumps finds zero raid traffic, so every byte here
	///         comes from the client's reader rather than from retail's sender. The framing is therefore as certain as
	///         static RE gets - a wrong shape desyncs the client's stream and it names the failure in its own assert -
	///         while the meaning of the tail is not established at all. See docs/protocol/gaps.md.
	///     </para>
	/// </remarks>
	public static class RaidMatchCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchYouJoinedRaid;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>
	///     One character's two raid flags, keyed by their ID32.
	/// </summary>
	/// <remarks>
	///     The client stores them in a map keyed by that id (<c>FUN_0055c0a0</c> inserts each element into a container
	///     rather than a vector), which is what makes the ID32 a key rather than a payload. What the two bools select is
	///     not recoverable from the reader. [V0]
	/// </remarks>
	public sealed record RaidMemberFlags(EntityId Character, bool FlagA, bool FlagB);

	/// <summary>
	///     What the client reads after the two lists: an ID32, a word, a flag, a second ID32, then three more words.
	/// </summary>
	/// <remarks>
	///     The reader gives their order and width and nothing else - it stores each straight into a field of the raid
	///     object, and the raid UI consumes them. Named by position rather than guessed at; a raid built here sends
	///     zeros, matching the empty flag list. [V0]
	/// </remarks>
	public sealed record RaidMatchTail(EntityId Ref1, uint Word1, bool Flag, EntityId Ref2, uint Word2, uint Word3, uint Word4)
	{
		public static RaidMatchTail Empty { get; } = new RaidMatchTail(new EntityId(0, 0), 0, false, new EntityId(0, 0), 0, 0, 0);
	}

	public sealed record RaidMatch(TeamRoster Raid, IReadOnlyList<TeamRoster> Teams, IReadOnlyList<RaidMemberFlags> MemberFlags, RaidMatchTail Tail);
}
