using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Protobuf;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The melee-combo runtime: turns the combo fields the client puts on every swing into a tracked attempt, so
	///     the finisher can carry the combo's effect.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The combo is on the wire explicitly. The swing's protobuf args are a <c>CommandMessage</c> (the client's
	///         own descriptor), and three of its fields are named for exactly this:
	///     </para>
	///     <code>
	///     field 7  combo_id           the `Combos` catalog id, verbatim
	///     field 8  combo_attack_num   the 0-based step, absent on the opener (protobuf omits a zero)
	///     field 9  combo_attack_hits  the hit count (never set in the corpus)
	///     </code>
	///     <para>
	///         So no client RE is needed to know which combo a player is performing: the client says so.
	///         [V3: ComboWireTests]
	///     </para>
	///     <para>
	///         <c>bcc_action_id</c> is the swing animation, and per class and weapon it does map one-to-one onto the
	///         step's direction (Assassin/Rogue dagger UL=49732 UR=49730 UP=49728; Herald/Conqueror/Soldier two-hander
	///         UL=41906 UR=41907 UP=41908). Nothing here needs it, because the combo and step are stated outright.
	///     </para>
	///     <para>
	///         Directions therefore never have to be matched against <c>Combo.Actions</c>: this service validates the
	///         step against the catalog sequence's length, and <see cref="ComboActionParser" /> stays the vocabulary
	///         for the catalog side.
	///     </para>
	/// </remarks>
	public sealed class ComboService
	{
		/// <summary>
		///     Parses the combo catalog and scaling at startup rather than on the first swing that consults them - see
		///     <see cref="BuffStatEffects.WarmAsync" />.
		/// </summary>
		public async Task WarmAsync()
		{
			_catalog ??= await LoadCatalog();
			_scaling ??= await LoadScaling();
		}

		/// <summary>
		///     The client floors a combo's animation length at 0.1 s ("Got negative or below 0.1s full combo animation
		///     duration; falling back to 0.1", <c>BCTCombo::GetComboAnimationLength</c>), so a combo whose steps report
		///     no activation time still costs stamina rather than dividing by zero.
		/// </summary>
		public const uint MinAnimationCentiseconds = 10;

		/// <summary>
		///     <c>combo_attack_num</c> on a combo with no key sequence to index into. See <see cref="TrackSwing" />.
		/// </summary>
		public const uint UnsteppedAttackNum = uint.MaxValue;

		private readonly ICatalogRepository _database;
		private readonly ILogger _logger;

		// The whole 744-row catalog and the 960-row scaling curve, cached on first use. Both are static content read
		// per swing, and the runtime has to resolve combo ids the player's own class does not own: a Herald performing
		// `Slave_1_AOE_2hb_Sweep_2` is a captured case, and it is not in his ClassCombo set.
		private Dictionary<uint, ComboEntry>? _catalog;
		private Dictionary<(ConanClass, uint), ClassComboScaling>? _scaling;

		public ComboService(ICatalogRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		/// <summary>
		///     Reads the combo fields off one <c>Combat:Melee</c> request and advances the session's running attempt.
		/// </summary>
		/// <returns>
		///     Null when the swing carries no combo id (an ordinary white swing) or names a combo the catalog does not
		///     hold - a plain swing is the safe reading of both, and the caller resolves it as one.
		/// </returns>
		public async Task<ComboSwing?> TrackSwing(INetworkClient client, CommandMessage args)
		{
			var session = client.Session;
			if (args is not { } message || !message.ShouldSerializeComboId() || message.ComboId == 0)
			{
				// A swing outside any combo ends the running attempt: the player broke the sequence with a plain hit,
				// so the next combo swing must start over rather than continue at a stale step.
				session.ActiveComboId = null;
				session.ActiveComboStep = 0;
				session.ActiveComboCentiseconds = 0;
				return null;
			}

			var catalog = _catalog ??= await LoadCatalog();
			if (!catalog.TryGetValue(message.ComboId, out var entry))
			{
				_logger.LogWarning($"Combo {message.ComboId} is not in the catalog - resolving the swing as a plain hit");
				return null;
			}

			// 0xFFFFFFFF is the unstepped sentinel, not a step: the corpus carries it on exactly the combos with no key
			// sequence to index into - the NPC `action` rows and the blank-Actions specials (`Combo_Charge_
			// soldier_charge`, `Mounted on Horse - Attack Mid_unmoving`). Every combo that has a sequence carries
			// {0 .. steps-1} and nothing else. Read as an int it would be -1 and index off the front of the sequence,
			// so it is collapsed to the single step it means.
			var step = message.ComboAttackNum == UnsteppedAttackNum ? 0 : (int)message.ComboAttackNum;

			// The reuse gate. A combo that opens while its own `Combo.Cooldown` is still running resolves as an
			// ordinary white swing: the combo payload is withheld and nothing else is sent. Retail's client will not
			// send a combo whose recast is still ticking, because its own timer greys the icon, so retail's server has
			// no captured answer to one and withholding the payload is the honest refusal. An on-screen refusal line is
			// not buildable - every FormatFeedback template resolves a client-validated content hash - and a chat line
			// is text retail never sends here, so this logs instead.
			//
			// Only the opener is gated. Once an attempt is running the steps are the client walking a sequence it
			// already committed to, and cutting it off mid-way would leave the player mid-combo with no finisher.
			//
			// The refused swing resolves through the plain-hit path, so its zone comes from the request tail's
			// direction ability rather than from the catalog (present on 408 of the 515 captured player swings). On the
			// minority that carry no ability word the ack falls back to Overhead - a fidelity loss confined to a case
			// retail's own client does not produce.
			if (step == 0 && RemainingCooldownMs(session, message.ComboId) is var remaining and > 0)
			{
				_logger.LogInformation("Combo {Combo} ({Name}) REFUSED: {Remaining} ms of its {Cooldown} s reuse time left.",
					message.ComboId, entry.Combo.Name, remaining, entry.Combo.Cooldown);
				session.ActiveComboId = null;
				session.ActiveComboStep = 0;
				session.ActiveComboCentiseconds = 0;
				return null;
			}

			// A step that does not continue the running attempt restarts it. Retail does this itself (the assassin
			// capture re-sends step 0 twice for one Cunning Strike), and the client is authoritative on which combo
			// is running, so this is bookkeeping - not a rejection.
			if (session.ActiveComboId != message.ComboId || step != session.ActiveComboStep)
			{
				session.ActiveComboId = message.ComboId;
				session.ActiveComboCentiseconds = 0;
			}

			session.ActiveComboCentiseconds += message.ActivationTime;
			session.ActiveComboStep = step + 1;

			var swing = new ComboSwing(entry.Combo, step, StepCountFor(entry, step), message.ActivationTime, session.ActiveComboCentiseconds,
				DirectionFor(entry, step));
			if (swing.IsFinisher)
			{
				// The attempt is over - the next combo swing opens a new one.
				session.ActiveComboId = null;
				session.ActiveComboStep = 0;
				session.ActiveComboCentiseconds = 0;

				// The reuse time starts when the combo goes off, not when it opens - a sequence the player breaks
				// half-way costs no cooldown. Same placement, and the same reasoning, as the spell recast arming in
				// the cast path.
				if (entry.Combo.Cooldown > 0)
					session.ComboReadyAt[message.ComboId] = Environment.TickCount64 + entry.Combo.Cooldown * 1000L;
			}

			return swing;
		}

		/// <summary>
		///     The catalog id of the <c>combo_npc_generic_*</c> row this NPC's melee swings carry, or 0 when it has no
		///     melee weapon.
		/// </summary>
		/// <remarks>
		///     The weapon picks the row (<see cref="NpcWeapon.MeleeComboName" />); this only resolves the name against
		///     the catalog, which is where the ids live - hardcoding nine ids would be a second copy of imported RDB
		///     content. Resolve it once per NPC at zone load rather than per swing, as with the ranged-attacker flag:
		///     the equipment rows are loaded there and the answer cannot change while the entity lives.
		/// </remarks>
		public async Task<uint> NpcMeleeComboId(IEnumerable<INpcEquipment>? equipment)
		{
			if (NpcWeapon.MeleeComboName(equipment) is not { } name)
				return 0;

			var catalog = _catalog ??= await LoadCatalog();
			foreach (var entry in catalog.Values)
				if (entry.Combo.Name == name)
					return entry.Combo.Id;

			// The nine generic rows come from RDB 1010206 and every one of them is in the shipped catalog, so a miss
			// means the combo import is short - say so rather than silently swinging plain forever.
			_logger.LogWarning("NPC combo {Name} is not in the catalog - the mob will swing without one", name);
			return 0;
		}

		/// <summary>
		///     The class-family rows this NPC may swing beside its generic one, level-gated.
		/// </summary>
		/// <remarks>
		///     <para>
		///         <paramref name="comboClass" /> is the harvested archetype (<c>NpcStatCore.ComboClass</c>) and is the
		///         selector when the corpus caught this template swinging one; without it the weapon answers the two
		///         buckets where the class family at that criteria is unique, and nothing else. Empty when neither
		///         speaks.
		///     </para>
		///     <para>
		///         The gate is <c>Combos.Level</c> verbatim, which is the family name's trailing number: across the 30
		///         captured class-family rows no actor ever swings one above its own level, and a level-75 mob still
		///         swings the rank-1 row, so this is a ceiling rather than a bracket. Resolve it once per NPC at zone
		///         load, like the generic id.
		///     </para>
		///     <para>
		///         The name prefix picks the rows and the client's own <c>npcclass</c> vetoes any whose stated class
		///         contradicts it - <c>combo_npc_conqueror_dual_1_life_reaper</c> says Dark Templar and
		///         <c>combo_npc_heimdall_fatality_1</c> says Barbarian, so a name is not the family. A row that states
		///         nothing is kept: the veto is a correction, never a narrowing, and one row
		///         (<c>combo_npc_assassin_death_whirlwind</c>) carries no <c>npcclass</c> at all.
		///     </para>
		/// </remarks>
		public async Task<uint[]> NpcClassComboIds(IEnumerable<INpcEquipment>? equipment, uint level, int? comboClass = null)
		{
			var catalog = _catalog ??= await LoadCatalog();

			// The harvested archetype, when the corpus caught this template swinging one. The class then comes from
			// `Combos.NpcClass` on both sides - the stat core says which class, the catalog says which rows are that
			// class - and the weapon only narrows within it, through the row's own `criteria`. That is what lets a
			// Hired Bodyguard swing `guardian_shield_*` with a sword and `guardian_polearm_*` with a polearm off one
			// column, and what stops a shield-bearing Barbarian (Vanir Champion Elite) being read as a Guardian the way
			// a weapon rule must.
			if (comboClass is { } archetype)
			{
				return catalog.Values
					.Where(e => e.Combo.Level <= level
					            && e.Combo.NpcClass == archetype
					            && NpcWeapon.MeetsComboCriteria(e.Combo.Criteria, equipment))
					.Select(e => e.Combo.Id)
					.Order()
					.ToArray();
			}

			// The fallback for the roughly 98% of templates no capture ever caught swinging a class family: the weapon,
			// for the two buckets where the class family at that weapon criteria is unique.
			// `NpcWeapon.ClassComboFamily` sets out the buckets it deliberately refuses.
			if (NpcWeapon.ClassComboFamily(equipment) is not { } family)
				return [];

			return catalog.Values
				.Where(e => e.Combo.Level <= level
				            && e.Combo.Name.StartsWith(family.NamePrefix, StringComparison.Ordinal)
				            && (e.Combo.NpcClass == null || e.Combo.NpcClass == (int)family.Class))
				.Select(e => e.Combo.Id)
				.Order()
				.ToArray();
		}

		/// <summary>
		///     Milliseconds left on this combo's reuse time, 0 when it is ready.
		/// </summary>
		/// <remarks>
		///     Keyed by the combo id rather than by a line: unlike a spell, whose ranks share one
		///     <c>SetSpellLineRecastTime</c> group, a combo's ranks replace one another in the book (you hold
		///     <c>Vicious Strike II</c> or <c>III</c>, never both), so there is no observable case of two ranks needing
		///     one shared timer. <c>Combo.ParentId</c> chains the tree should that turn out to be wrong.
		/// </remarks>
		private static long RemainingCooldownMs(INetworkClientSession session, uint comboId)
		{
			return session.ComboReadyAt.TryGetValue(comboId, out var readyAt) ? Math.Max(0, readyAt - Environment.TickCount64) : 0;
		}

		/// <summary>
		///     The stamina a completed combo costs, by the client's own formula (FUN_007d7ba0):
		///     <c>MaxStamina(0x1F9) x (Combo.Stamina / 100) x animationLength / ClassComboScaling.StaminaDivisor</c>.
		/// </summary>
		/// <remarks>
		///     The client gates starting a combo on this cost being affordable (FUN_007d5930), so the number is real
		///     gameplay rather than a tooltip, which is what separates it from the DPS half of the same table (see
		///     <see cref="ClassComboScaling" />).
		/// </remarks>
		public async Task<uint> StaminaCost(ConanClass @class, uint level, uint maxStamina, ComboSwing swing)
		{
			if (maxStamina == 0 || swing.Combo.Stamina <= 0)
				return 0;

			var scaling = _scaling ??= await LoadScaling();
			// The client falls back to a divisor of 1.0 and logs "MISSING stamina lookup duration for combo" when
			// the row is absent or non-positive; mirror that rather than dividing by zero.
			var divisor = scaling.TryGetValue((@class, level), out var row) && row.StaminaDivisor > 0 ? row.StaminaDivisor : 1;
			return (uint)(maxStamina * (swing.Combo.Stamina / 100f) * swing.AnimationSeconds / divisor);
		}

		/// <summary>
		///     The zone this step is swung from, straight off the catalog.
		/// </summary>
		/// <remarks>
		///     A combo step names no direction ability on the wire - word 5 is absent on 107 of the 515 captured player
		///     swings - but the result record still carries a direction code on every one of them
		///     (<c>--report bcc-attack-tail</c>: 0xCA x58, 0xCB x22, 0xC9 x8 on the ability-less player swings). A
		///     swing is therefore always directed, and for a combo step the catalog is where the direction comes from,
		///     because the client picked the step by pressing that key.
		/// </remarks>
		/// <returns>
		///     Null for a combo with no key sequence: the <c>action</c> NPC rows, the blank specials, or a
		///     <c>Thrust</c> step.
		/// </returns>
		private static SwingDirection? DirectionFor(ComboEntry entry, int step)
		{
			return step >= 0 && step < entry.Sequence.Count ? entry.Sequence[step].ToSwingDirection() : null;
		}

		private static int StepCountFor(ComboEntry entry, int step)
		{
			// A combo whose Actions is a marker or blank has no key sequence to count: the 69 `action` rows are the NPC
			// combos and the 160 blank rows the monster and mounted specials. Treat the swing as its own finisher so
			// the effect still lands, which is what the single-step NPC combos in the corpus do.
			return entry.StepCount > 0 ? entry.StepCount : step + 1;
		}

		private async Task<Dictionary<uint, ComboEntry>> LoadCatalog()
		{
			var clock = System.Diagnostics.Stopwatch.StartNew();
			var combos = await _database.GetCombos();
			var map = new Dictionary<uint, ComboEntry>(combos.Length);
			foreach (var combo in combos)
				map[combo.Id] = new ComboEntry(combo, ComboActionParser.TryParse(combo.Actions, out var sequence) ? sequence : []);

			_logger.LogInformation("ComboService: {Count} combos loaded in {Ms} ms (once per server).", map.Count, clock.ElapsedMilliseconds);
			return map;
		}

		private async Task<Dictionary<(ConanClass, uint), ClassComboScaling>> LoadScaling()
		{
			var rows = await _database.GetClassComboScalings();
			var map = new Dictionary<(ConanClass, uint), ClassComboScaling>(rows.Length);
			foreach (var row in rows)
				map[(row.Class, row.Level)] = row;

			return map;
		}

		// A catalog row plus its parsed key sequence, so the sequence is parsed once at load rather than per swing.
		// Empty for a combo with no directional grammar: a reactive or magic marker, or a blank `Actions`.
		private readonly record struct ComboEntry(Combo Combo, IReadOnlyList<ComboInput> Sequence)
		{
			public int StepCount => Sequence.Count;
		}
	}
}
