using System;
using System.Collections.Generic;
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
	// The melee-combo runtime (#139): turns the combo fields the client puts on every swing into a tracked attempt,
	// so the finisher can carry the combo's effect.
	//
	// ⚠️ THE COMBO IS ON THE WIRE, EXPLICITLY. The long-standing read of the swing body - "[08][animByte][2B
	// marker][u32 counter][u32 cid = 0x300038XX]", with the note that the cid->combo and animByte->direction maps
	// were computed client-side and needed Ghidra - was a RAW-BYTE misread of the record's protobuf args. The args
	// are a `CommandMessage` (the client's own descriptor), and three of its fields are named for exactly this:
	//
	//     field 7  combo_id           the `Combos` catalog id, verbatim
	//     field 8  combo_attack_num   the 0-based step, ABSENT on the opener (protobuf omits a zero)
	//     field 9  combo_attack_hits  the hit count (never set in the corpus)
	//
	// The "cid 0x300038XX" was `[30 00]` (execution_status = 0) followed by `[38 <combo_id varint>]`, and the
	// "animByte" was the first byte of `bcc_action_id`'s varint. So no client RE is needed to know which combo a
	// player is performing: the client says so. [V3: ComboWireTests]
	//
	// What `bcc_action_id` actually is - since it is what the old read was reaching for - is the swing ANIMATION,
	// and per class+weapon it does map one-to-one onto the step's direction (Assassin/Rogue dagger UL=49732
	// UR=49730 UP=49728; Herald/Conqueror/Soldier two-hander UL=41906 UR=41907 UP=41908). It is not needed for
	// anything here, because the combo and step are stated outright.
	//
	// Directions therefore never have to be MATCHED against `Combo.Actions`: this service validates the step
	// against the catalog sequence's LENGTH, and `ComboActionParser` stays the vocabulary for the catalog side.
	public sealed class ComboService
	{
		// The client floors a combo's animation length at 0.1 s ("Got negative or below 0.1s full combo animation
		// duration; falling back to 0.1", BCTCombo::GetComboAnimationLength) - so a combo whose steps report no
		// activation time still costs stamina rather than dividing by zero.
		public const uint MinAnimationCentiseconds = 10;

		// `combo_attack_num` on a combo with no key sequence to index into. See TrackSwing.
		public const uint UnsteppedAttackNum = uint.MaxValue;

		private readonly IDatabaseRepository _database;
		private readonly ILogger _logger;

		// The whole 744-row catalog + the 960-row scaling curve, cached on first use. Both are static content and
		// both are read per swing, and the runtime has to resolve combo ids the player's own class does not own:
		// a Herald performing `Slave_1_AOE_2hb_Sweep_2` is a captured case, and it is not in his ClassCombo set.
		private Dictionary<uint, ComboEntry>? _catalog;
		private Dictionary<(ConanClass, uint), ClassComboScaling>? _scaling;

		public ComboService(IDatabaseRepository database, ILogger logger)
		{
			_database = database;
			_logger = logger;
		}

		// Reads the combo fields off one Combat:Melee request and advances the session's running attempt.
		// Returns null when the swing carries no combo id (an ordinary white swing) or names a combo the catalog
		// does not hold - a plain swing is the safe reading of both, and the caller resolves it as one.
		public async Task<ComboSwing?> TrackSwing(INetworkClient client, CommandMessage args)
		{
			var session = client.Session;
			if (args is not { } message || !message.ShouldSerializeComboId() || message.ComboId == 0)
			{
				// A swing outside any combo ENDS the running attempt: the player broke the sequence with a plain
				// hit, so the next combo swing must start over rather than continue at a stale step.
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

			// 0xFFFFFFFF is the UNSTEPPED sentinel, not a step: the corpus carries it on exactly the combos with no
			// key sequence to index into - the NPC `action` rows and the blank-Actions specials (`Combo_Charge_
			// soldier_charge`, `Mounted on Horse - Attack Mid_unmoving`). Every combo that HAS a sequence carries
			// {0 .. steps-1} and nothing else. Read as an int it would be -1 and index off the front of the
			// sequence, so it is collapsed to the single step it means.
			var step = message.ComboAttackNum == UnsteppedAttackNum ? 0 : (int)message.ComboAttackNum;

			// #623: the reuse gate. A combo that OPENS while its own `Combo.Cooldown` is still running resolves as an
			// ordinary white swing - the combo payload is withheld and nothing else is sent. That refusal shape is
			// #271's, for the same reason: retail's client will not SEND a combo whose recast is still ticking (its
			// own timer greys the icon), so retail's server has no captured answer to one and the honest refusal is
			// to withhold the payload rather than invent a reply. The on-screen refusal line #623 asked for does not
			// exist as something we can build - every FormatFeedback template resolves a client-validated content
			// hash (#664) - and a chat line is text retail never sends here, so this logs instead.
			//
			// Only the OPENER is gated. Once an attempt is running the steps are the client walking a sequence it
			// already committed to, and cutting it off mid-way would leave the player mid-combo with no finisher.
			//
			// The refused swing resolves through the plain-hit path, so its zone comes from the request tail's
			// direction ability rather than from the catalog (#673's first source, present on 408 of the 515 captured
			// player swings). On the minority that carry no ability word the ack falls back to Overhead - a fidelity
			// loss confined to a case retail's own client does not produce.
			if (step == 0 && RemainingCooldownMs(session, message.ComboId) is var remaining && remaining > 0)
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

				// #623: the reuse time starts when the combo GOES OFF, not when it opens - a sequence the player
				// breaks half-way costs no cooldown. Same placement, and the same reasoning, as the spell recast
				// arming in the cast path (#271).
				if (entry.Combo.Cooldown > 0)
					session.ComboReadyAt[message.ComboId] = Environment.TickCount64 + entry.Combo.Cooldown * 1000L;
			}

			return swing;
		}

		// #623: the catalog id of the `combo_npc_generic_*` row this NPC's melee swings carry, or 0 when it has no
		// melee weapon. The WEAPON picks the row (NpcWeapon.MeleeComboName); this only resolves the name against the
		// catalog, which is where the ids live - hardcoding nine ids would be a second copy of imported RDB content.
		//
		// Resolved ONCE per NPC at zone load rather than per swing, like `RangedAttacker`: the equipment rows are
		// Included there and the answer cannot change while the entity lives.
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

		// Milliseconds left on this combo's reuse time, 0 when it is ready. Keyed by the COMBO id rather than by a
		// line: unlike a spell, whose ranks share one `SetSpellLineRecastTime` group, a combo's ranks replace one
		// another in the book (you hold `Vicious Strike II` OR `III`, never both), so there is no observable case of
		// two ranks needing one shared timer. Combo.ParentId does chain the tree if that ever turns out to be wrong.
		private static long RemainingCooldownMs(INetworkClientSession session, uint comboId)
		{
			return session.ComboReadyAt.TryGetValue(comboId, out var readyAt) ? Math.Max(0, readyAt - Environment.TickCount64) : 0;
		}

		// The stamina a completed combo costs, by the client's own formula (FUN_007d7ba0):
		//     MaxStamina(0x1F9) x (Combo.Stamina / 100) x animationLength / ClassComboScaling.StaminaDivisor
		// The client gates STARTING a combo on this cost being affordable (FUN_007d5930), so the number is real
		// gameplay rather than a tooltip - which is what separates it from the DPS half of the same table (see
		// ClassComboScaling).
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

		// The zone this step is swung from, straight off the catalog (#673). A combo step names no direction ABILITY
		// on the wire - word 5 is absent on 107 of the 515 captured player swings - but the result record still
		// carries a direction code on every one of them (`--report bcc-attack-tail`: 0xCA x58, 0xCB x22, 0xC9 x8 on
		// the ability-less player swings). So a swing is ALWAYS directed, and for a combo step the catalog is where
		// the direction comes from: the client picked the step by pressing that key.
		//
		// Null for a combo with no key sequence (the `action` NPC rows, the blank specials, a `Thrust` step).
		private static SwingDirection? DirectionFor(ComboEntry entry, int step)
		{
			return step >= 0 && step < entry.Sequence.Count ? entry.Sequence[step].ToSwingDirection() : null;
		}

		private static int StepCountFor(ComboEntry entry, int step)
		{
			// A combo whose Actions is a marker or blank has no key sequence to count (the 69 `action` rows are the
			// NPC combos and the 160 blank rows the monster/mounted specials). Treat the swing as its own finisher
			// so the effect still lands - which is what the single-step NPC combos in the corpus do.
			return entry.StepCount > 0 ? entry.StepCount : step + 1;
		}

		private async Task<Dictionary<uint, ComboEntry>> LoadCatalog()
		{
			var combos = await _database.GetCombos();
			var map = new Dictionary<uint, ComboEntry>(combos.Length);
			foreach (var combo in combos)
				map[combo.Id] = new ComboEntry(combo, ComboActionParser.TryParse(combo.Actions, out var sequence) ? sequence : []);

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

		// A catalog row plus its parsed key SEQUENCE, so the sequence is parsed once at load rather than per swing.
		// Empty for a combo with no directional grammar (a reactive/magic marker, or a blank `Actions`).
		private readonly record struct ComboEntry(Combo Combo, IReadOnlyList<ComboInput> Sequence)
		{
			public int StepCount => Sequence.Count;
		}
	}
}
