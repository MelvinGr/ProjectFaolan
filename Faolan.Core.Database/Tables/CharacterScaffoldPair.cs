using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Database.Tables
{
	// One pair of the spawn scaffold's two ending [u32 id][s32 value] lists (ids are decades 10..110; only
	// progressed captured characters have any - fresh characters have both lists empty). ListIndex selects the
	// first (0) or second (1) wire list. See docs/re/player-spawn-generation.md "VALIDATED GRAMMAR".
	//
	// ListIndex 0 is the faction-reputation block: PairId = faction id, Value = the signed standing (out of
	// +/-50000) shown in the client's Factions window (proven in-client on the L80 necro NecroL80: PairId 10 =
	// Children of Yag-kosha -14375, 20 = Yellow Priests of Yun +14375, 60 = Scarlet Circle +25325, ...). So faction
	// standings ride the player's FA015807 spawn scaffold (PlayerSpawnScaffold.EndPairs -> PlayerSpawnBuilder),
	// imported from the capture; there is no separate faction table yet - a CharacterFaction model, a faction
	// catalog and standing changes from quests/kills are still to come. Open work tracked in Gitea #315.
	public class CharacterScaffoldPair : BaseEntity
	{
		public uint CharacterId { get; set; }

		[ForeignKey(nameof(CharacterId))]
		public virtual Character Character { get; set; } = null!;

		// For ListIndex 0: the faction id (10 Children of Yag-kosha, 20 Yellow Priests of Yun, 30 Shadows of Jade,
		// 40 Brittle Blade, 50 Last Legion, 60 Scarlet Circle, 90 Wolves of the Steppes, ...).
		public uint PairId { get; set; }

		// 0 = the first ending pair list (faction standings, see class note); 1 = the second.
		public byte ListIndex { get; set; }

		// For ListIndex 0: the signed faction standing (out of +/-50000). Friendly > 0, Hostile < 0.
		public int Value { get; set; }
	}
}
