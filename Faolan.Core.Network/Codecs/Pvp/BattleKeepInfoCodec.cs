using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Pvp
{
	/// <summary>
	///     <c>DispatchBattleKeepInfoRequestReturnPhase3And4</c> - the s2c answer to c2s
	///     <c>RequestBattleKeepInformation</c>, the per-battlekeep status query that sits beside the guild leaderboard
	///     (<see cref="MassivePvpInfoCodec" />). The client opens the Border Kingdoms siege panel and queries one keep
	///     at a time.
	/// </summary>
	/// <remarks>
	///     <c>NecroL80_..._gameplay_annotated_9</c> carries four request/reply pairs, each immediately followed by a
	///     <c>DispatchGuildRankingsUpdate</c> naming the owning guild ("Vendetta", "Hand of Stygia", "Fleurs de Lys"),
	///     so the reply is the keep's numeric status and the owning guild's name arrives separately. The grammar is
	///     byte-exact on all four captured replies:
	///     <code>
	///     [list frame]  { count x entry }
	///     entry = [u32 keepId][u32 guildClass][u32 owningGuildId][u32 attackerClass][u32 attackerGuildId]
	///             [u32 nextPhaseUnix][u32 phase]
	///     </code>
	///     <para>
	///         The columns are named from the client's own view rather than inferred from values. The reader
	///         <c>FUN_0134f280</c> scatters the seven words into a 0x1c struct in slot order [0][1][2][5][6][4][3], and
	///         the battlekeep window's filler <c>FUN_01315130</c>
	///         (<c>Views/MassivePvP/MassivePvPBattleKeepView.xml</c>) reads that struct: slots [1] and [2] are passed
	///         as one ID32 to the guild-name lookup and rendered under
	///         <c>MassivePvP_BattleKeepView_DefendingGuild</c>, so the second wire word is the guild ID32's class
	///         word rather than a zone; slot [3] (wire word 7) is the phase, named by <c>FUN_01315780</c> from LDB
	///         category 10032 under <c>MassivePvP_BattleKeepView_Phase</c>; slot [4] (wire word 6) is the time the next
	///         phase begins, rendered as weekday plus hh:mm under <c>MassivePvP_BattleKeepView_NextPhase</c>, with a
	///         countdown in phase War; slots [5] and [6] (wire words 4-5) are a second ID32, compared against the null
	///         ID32 and shown as the attacking guild, zero on every captured keep.
	///     </para>
	///     <para>
	///         The owner is cross-corroborated: keep 4's 0x7B726 is "Vendetta"'s guild id and keep 2's 0x7C6A6 is
	///         "Hand of Stygia"'s, both matching the guildId column of the same session's Massive-PvP board and the
	///         guild name in the <c>DispatchGuildRankingsUpdate</c> that follows each reply.
	///     </para>
	///     <para>
	///         The Phase1And2 twin has no captured body. Its layout is recovered from the client reader
	///         (<c>FUN_013b1680</c>) in docs/subsystems/guild-cities-and-sieges.md and is not built here, because it
	///         carries a nested attacker-ranking list.
	///     </para>
	///     <para>
	///         The attacker pair is carried as two raw words so that a reply which fills them round-trips rather than
	///         being rejected; the owner's class word is asserted, because every captured keep carries it and a keep
	///         without an owner has no meaning in this reply. No siege layer runs here, so nothing builds this
	///         in-world; the codec exists so the decode is byte-exact and citable.
	///     </para>
	/// </remarks>
	public static class BattleKeepInfoCodec
	{
		public const uint Method = (uint)CSPlayerInterfaceMethods.DispatchBattleKeepInfoRequestReturnPhase3And4;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(BattleKeepInfo value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Keeps.Count));
			foreach (var keep in value.Keeps)
				Write(s, keep);

			return s.ToArray();
		}

		public static BattleKeepInfo Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(BattleKeepInfoCodec));
			var count = r.ReadListCount();
			var keeps = new List<BattleKeepInfoEntry>(count);
			for (var i = 0; i < count; i++)
				keeps.Add(Read(ref r));

			r.EnsureAtEnd();

			return new BattleKeepInfo(keeps);
		}

		internal static BattleKeepInfoEntry Read(ref SpanReader r)
		{
			var keepId = r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Guild, "the owning guild's entity class");
			var owningGuildId = r.ReadU32Be();
			var attackerClass = r.ReadU32Be();
			var attackerGuildId = r.ReadU32Be();
			var nextPhaseUnix = r.ReadU32Be();
			var phase = (BattleKeepPhase)r.ReadU32Be();
			return new BattleKeepInfoEntry(keepId, owningGuildId, attackerClass, attackerGuildId, nextPhaseUnix, phase);
		}

		internal static void Write(FaolanStream s, BattleKeepInfoEntry keep)
		{
			s
				.WriteUInt32(keep.KeepId)
				.WriteUInt32(EntityClass.Guild)
				.WriteUInt32(keep.OwningGuildId)
				.WriteUInt32(keep.AttackingGuildClass)
				.WriteUInt32(keep.AttackingGuildId)
				.WriteUInt32(keep.NextPhaseUnix)
				.WriteUInt32((uint)keep.Phase);
		}
	}

	public sealed record BattleKeepInfo(IReadOnlyList<BattleKeepInfoEntry> Keeps)
	{
		// The reply a server with no siege layer sends. Every captured entry names an owning guild and an unowned
		// keep's encoding is uncaptured, so zero entries is the only honest body.
		public static readonly BattleKeepInfo Empty = new BattleKeepInfo([]);
	}

	/// <summary>
	///     One keep's status. <c>AttackingGuildClass</c> and <c>AttackingGuildId</c> are the attacker's ID32 as two raw
	///     words: <c>[0][0]</c> is the client's null ID32 (no war declared, as on every captured keep), while a real
	///     attacker carries the guild entity class and its id.
	/// </summary>
	public sealed record BattleKeepInfoEntry(
		uint KeepId,
		uint OwningGuildId,
		uint AttackingGuildClass,
		uint AttackingGuildId,
		uint NextPhaseUnix,
		BattleKeepPhase Phase);

	/// <summary>
	///     The battlekeep's siege phase, named by the client from LDB category 10032
	///     (<c>--report ldb-search --report-arg 10032:&lt;letter&gt;</c>).
	/// </summary>
	/// <remarks>
	///     The two reply methods are named for the phases they answer in: the keep window shows the attacker ranking
	///     list (the Phase1And2 record's nested list) below phase 2 and the single attacking guild from phase 2 on.
	///     Every captured keep is in <see cref="SilenceBeforeTheStorm" />.
	/// </remarks>
	public enum BattleKeepPhase : uint
	{
		WarDeclaration = 0,
		Skirmish = 1,
		SilenceBeforeTheStorm = 2,
		War = 3,
		Aftermath = 4,
	}
}
