using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Constants;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The spellweaving state machine (docs/subsystems/combos.md). Spellweaving is the caster analogue of the melee combo:
	// the player ACTIVATES the weave (entering a trance), ADDS components one at a time while in the trance, then
	// DEACTIVATES to fire the accumulated blend. Decoded wire (tools/capture/decode_spellweave.py):
	//   - activate/deactivate = c2s PerformSpecialAction (0x205A), a TOGGLE (body [u32 0x000000fc]);
	//   - add-component       = c2s CombatReactiveIIR_c (0x54C02089), body carrying the componentIndex;
	//   - fire                = s2c 0xF98E10B3 burst on deactivate; the 0xE9F24184 meter drains during the trance.
	//
	// On ACTIVATE the server must answer with the trance-open burst or the client never enters the interactive
	// trance (it stalls at "started" and never sends the adds): the trance buffs (ApplySpell) + the reactive element
	// LIST (which declares the addable weave elements the componentIndex indexes into). This service emits that burst
	// via ISpellweaveOutbound, the port the listener implements. The trance immobilisation IS wired (the CharDCMove
	// handler pins the caster's position while IsSpellweaving).
	//
	// ⭐ The per-class TABLE this runs on is the client's own, recovered for #141 - see SpellweaveCatalog. All four
	// weaving classes have a profile now, the stack is a ladder of six distinct buffs rather than a base plus an
	// offset, and each rung rolls a backlash off a six-way partition of one draw. What is still NOT modelled is the
	// rung's own stat payload: the ladder's `Modify BaseSpellDamage` step (250 per rung for the necromancer, 100 for
	// the other three), its `Modify NaturalEnergyRegeneration -15000`, and the `SpellLineStatModifier` splash it
	// grants the class's spell lines. Those are ordinary GameAction elements on the rung's own record, so they land
	// when the generic element engine walks a CastNano chain and evaluates criteria (Gitea #784 / #791) rather than
	// through anything bespoke here.
	public sealed class SpellweaveService
	{
		private const int DotDrainPerTick = 31_705;

		// How long after a pick the component picker stays hidden before the server re-sends the reactive list to bring
		// it back (capture: add#0 105.8s -> list re-send 114.0s, ~8s).
		private const int ElementCooldownMs = 8000;

		// Trance drain: the yellow ENERGY bar drains while weaving. The bar is moved by DispatchUpdateSprintbar (0x200E,
		// ISpellweaveOutbound.SendSprintBar) - full = 9000 for the necro - alongside the 0x37E68C "Spellweaving Cost" gauge (both retail
		// sends, same token). The drain accelerates as the weave goes on (the capture's -90 -> -270). When energy hits
		// 0 the health DoT (0x37E690) kicks in, draining ~31705 health/tick - the damage for over-weaving. Streamed on
		// a self-rescheduling timer; the DoT is visual (drains the health bar) - authoritative vitals/death is a follow-up.
		private const int EnergyBaseDrain = 90;
		private const int EnergyDrainAccelPerTick = 6; // -90 -> ~-270 over the weave, matching the capture curve
		private const int EnergyDrainMax = 270;
		private const int TranceMeterIntervalMs = 1500;

		private const uint DotStartHealth = 602_395; // ~necro health when the DoT began (approx; should be real health)

		// The "Exhausted" debuff applied when the energy pool hits 0. Shared with the SPRINT path, which retail drives
		// the same way - see SpellweaveCatalog.ExhaustedBuff for the capture that confirms the id and both twins.
		private const uint ExhaustedBuff = SpellweaveCatalog.ExhaustedBuff;
		private readonly ILogger _logger;
		private readonly ISpellweaveOutbound _out;

		public SpellweaveService(ILogger logger, ISpellweaveOutbound outbound)
		{
			_logger = logger;
			_out = outbound;
		}

		// A component add (CombatReactiveIIR) - only meaningful inside an open trance. componentIndex indexes into the
		// caster's weave-element list. Retail answers each add with a burst (tools/capture/decode_spellweave.py, frames
		// 1657-1661): the component's OWN spell applied immediately as a self-buff (necro: add idx0->Arcane Renewal
		// 0x396AE9, idx1->Arcane Surge 0x3969D8, idx3->Death God 0x396A22 - so necro components take effect as you
		// weave them), then a re-sent element list. Without that response the client only ever registers the FIRST
		// pick (it never gets the ack that re-arms it). The animation BCC pair in that burst is not built yet.
		public async Task AddComponentAsync(INetworkClient client, uint componentIndex)
		{
			var session = client.Session;
			if (session.Character == null || session.Account == null)
				return;

			if (!session.IsSpellweaving)
			{
				_logger.LogInformation("Spellweave component #{Idx} ignored - no open weave for '{Name}'.", componentIndex, session.Character.Name);
				return;
			}

			var profile = SpellweaveCatalog.ForClass(session.Character.Class);
			var caster = session.Character.Id;
			session.WeaveComponents.Add(componentIndex);
			_logger.LogInformation("Spellweave ADD component #{Idx} (#{Count}) for '{Name}'.", componentIndex, session.WeaveComponents.Count, session.Character.Name);

			var hasElement = profile != null && componentIndex < profile.WeaveElements.Length;

			// The component-add gesture animation (BCC begin + complete), carrying the added element.
			if (hasElement)
			{
				await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveComponentAdd(caster, profile!.WeaveElements[componentIndex], false)));
				await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveComponentAdd(caster, profile.WeaveElements[componentIndex], true)));
			}

			// Immediate component effect: apply the element's own spell as a self-buff (the full id is the base << 8).
			if (hasElement)
				await _out.ApplyBuff(client, caster, caster, profile!.WeaveElements[componentIndex] << 8);

			// Do NOT re-send the reactive list now: the client HIDES the component picker itself once a component is
			// picked (the capture sends no list after an add - the next one is frame 1705, ~8s later). Re-sending it
			// immediately is what kept the picker up and let the player spam. Instead bring the picker back after the
			// cooldown by re-sending the full list, so the flow is: pick -> picker disappears -> ~cooldown -> reappears.
			if (profile != null)
			{
				Task.RunAfter(ElementCooldownMs, async () =>
				{
					var session = client.Session;
					if (session.Account == null || !session.IsSpellweaving)
						return;

					// Advance the weave one RUNG - `Spellweaving (1)` -> `(2)` -> ... The new buff overlaps the
					// previous one still ticking down, which is the brief x2 the user sees. Each rung is a
					// distinct buff off the class ladder, NOT the same buff at an incremented id: only the
					// necromancer's six happen to be contiguous, and two of the four classes would walk into
					// unrelated spells under arithmetic (see SpellweaveCatalog.StackLadder).
					session.SpellweaveBuffStack++;
					await _out.ApplyBuff(client, caster, caster, SpellweaveCatalog.RungBuff(profile, session.SpellweaveBuffStack) << 8);

					// ... and roll THIS rung's backlash. The bands widen as the stack climbs, so the sixth
					// component cannot escape one.
					await RollBacklash(client, profile, caster, session.SpellweaveBuffStack);

					await WorldIir.Send(client, CombatReactiveCodec.Build(caster, profile.WeaveElements));
				}, _logger);
			}

			await Feedback(client, $"Weave component added ({session.WeaveComponents.Count}).");
		}

		// The activate/deactivate toggle (PerformSpecialAction). Opens the trance on the first press, fires + closes
		// it on the second. `param` is the toggle's body word (0xfc in the capture) - kept for the log / future use.
		public async Task ToggleAsync(INetworkClient client, uint param)
		{
			var session = client.Session;
			if (session.Character == null || session.Account == null)
				return;

			if (!session.IsSpellweaving)
			{
				await StartAsync(client, param);
				return;
			}

			await FireAsync(client);
		}

		private Task Feedback(INetworkClient client, string message)
		{
			return _out.SendFeedback(client, message);
		}

		// Roll the rung's backlash and apply the outcome, or nothing when the roll escapes.
		//
		// The table is the client's own (SpellweaveCatalog.RollBacklash): ONE draw of 0..99 partitioned six ways,
		// so at most one backlash can land per rung. The randomness lives here rather than in the catalog so the
		// partition itself stays a pure function a test can walk end to end.
		private async Task RollBacklash(INetworkClient client, SpellweaveProfile profile, uint caster, int rung)
		{
			var roll = Random.Shared.Next(100);
			var backlash = SpellweaveCatalog.RollBacklash(profile, rung, roll);
			if (backlash == null)
			{
				_logger.LogInformation("Spellweave backlash escaped at rung {Rung} (roll {Roll}).", rung + 1, roll);
				return;
			}

			_logger.LogInformation("Spellweave BACKLASH at rung {Rung} (roll {Roll}): spell {Spell}.", rung + 1, roll, backlash);
			await _out.ApplyBuff(client, caster, caster, backlash.Value << 8);
		}

		// Deactivate: close the trance. There is no separate FIRE payload to send, which is the other half of what
		// #141 was waiting on: the weave's whole reward is the `Spellweaving (N)` rung buff it leaves running, and
		// that buff's duration is the reward window (12s at rung 1, rising to 32s at rung 5). Deactivating simply
		// ends the trance and lets the rung tick down.
		//
		// ⚠️ **The "fire burst" is four COMBAT-RATING stat sets, and replaying it is almost certainly wrong** (#678).
		// The four ids are `AttackRating` / `MagicRating` / `DefenseRating` / `ResistRating` and the value is the
		// captured character's own rating (31700 for that level-80 necromancer), not a burst constant - so what this
		// sends any caster is "your four combat ratings are now 31700". It is left byte-identical to what it sent
		// before the decode rather than silently changed, because what retail's weave SHOULD push is its own
		// question - tracked in Gitea #685.
		private async Task FireAsync(INetworkClient client)
		{
			var session = client.Session;
			var profile = SpellweaveCatalog.ForClass(session.Character.Class);
			var caster = session.Character.Id;
			var components = string.Join(",", session.WeaveComponents);
			_logger.LogInformation("Spellweave FIRE for '{Name}': {Count} components [{Components}].", session.Character.Name, session.WeaveComponents.Count, components);

			// Clear IsSpellweaving FIRST so the meter/add loops stop before we tear the trance down.
			session.IsSpellweaving = false;

			// ⚠️ The four StatIIR records this used to send on fire are GONE (#685), and they were never the weave's.
			// They were modelled as opaque "fire stages" 0x398/0x39B/0x39A/0x39D with a constant 0x7BD4 trailer; the ids
			// are the client's AttackRating / MagicRating / DefenseRating / MagicalResist and the "constant" was that
			// level-80 necromancer's own rating. Across the corpus those records appear in 31 captures, and 20 of them
			// contain no spellweaving at all - including a Herald of Xotli, which cannot weave. They are the level-scaled
			// rating quad (CombatRatingCurve, `400 x level - 300`) and the weave capture is simply where one was held.
			// So a weave pushes nothing here; its real effect is the componentIndex -> effect table, still open on #141.

			// The trance-EXIT animation: take the character out of the weave pose (without it it stays stuck kneeling).
			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveTranceExit(caster)));

			// Close the trance: remove the trance buffs (their rank-1 end-twins) so the client clears the icons + exits
			// the trance pose (capture: the cancel press removes 37F298 + the current 37E5A8 stack). Without this the
			// client stays in the trance visually even though the server considers the weave done (#the cancel bug).
			if (profile != null)
			{
				await _out.RemoveBuff(client, caster, caster, profile.TranceBuff << 8);
				await _out.RemoveBuff(client, caster, caster, SpellweaveCatalog.RungBuff(profile, session.SpellweaveBuffStack) << 8);

				// ⚠️ TWO removals, not three. This used to also remove Arcane Drought, on the reading that it was
				// a trance buff; it is a backlash ROLL OUTCOME with a 10s duration of its own, and the capture's
				// cancel press removes exactly 0x37F298 and the current rung. Ending a backlash early because the
				// player cancelled is the opposite of what the mechanic is for.
			}

			await Feedback(client, $"Spellweave fired with {session.WeaveComponents.Count} components.");

			// The trance root is already released: IsSpellweaving was cleared above, so the move handler stops
			// pinning the caster.
			session.WeaveComponents.Clear();
			session.SpellweaveBuffStack = 0;
		}

		// Streams the trance drain while the trance is open. Phase 1: the yellow ENERGY bar drains from the current
		// tracked value with an accelerating rate. Phase 2 (once energy is empty): the Exhausted debuff + the health
		// DoT gauge (the damage for over-weaving). Stops as soon as IsSpellweaving clears (deactivate / logout). A
		// PeriodicTimer loop; packets are byte-exact (SpellweaveCodec.BuildStatGauge), the curves are approximate.
		private async Task RunTranceMeter(INetworkClient client, uint caster)
		{
			var health = DotStartHealth;
			var tick = 0;
			var exhausted = false;

			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TranceMeterIntervalMs));
				while (await timer.WaitForNextTickAsync())
				{
					var session = client.Session;
					if (session.Account == null || !session.IsSpellweaving)
						return; // trance closed

					if (session.Energy > 0)
					{
						// Phase 1: energy drains from the CURRENT tracked value (not always full), accelerating as the
						// weave goes on. The 0x200E sprint-bar update moves the yellow bar; the 0x37E68C gauge is the
						// parallel accounting (both carry the same token). session.Energy is the shared, tracked pool.
						var drain = (uint)Math.Min(EnergyBaseDrain + tick * EnergyDrainAccelPerTick, EnergyDrainMax);
						drain = Math.Min(drain, session.Energy);
						session.Energy -= drain;
						await _out.SendSprintBar(client, session.Energy);
						await WorldIir.Send(client, StatDamageCodec.Build(StatDamages.SpellweaveGauge(caster, session.Energy, -(int)drain, StatDamages.SpellweaveStaminaGauge, StatDamages.SpellweaveStaminaScale)));
					}
					else
					{
						// Energy just hit 0 -> apply the Exhausted debuff once (the depletion debuff).
						if (!exhausted)
						{
							exhausted = true;
							await _out.ApplyBuff(client, caster, caster, ExhaustedBuff);
						}

						// Phase 2: energy empty -> the health DoT (the damage the player takes for over-weaving).
						var dmg = (uint)Math.Min(DotDrainPerTick, (int)health);
						health -= dmg;
						await WorldIir.Send(client, StatDamageCodec.Build(StatDamages.SpellweaveGauge(caster, health, -(int)dmg, StatDamages.SpellweaveDotGauge, StatDamages.SpellweaveDotScale)));
					}

					tick++;
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Trance meter loop failed.");
			}
		}

		// Open the trance: resolve the caster's class profile and emit the activate-response burst (trance buffs +
		// reactive element list) so the client enters the interactive trance and can send its adds.
		private async Task StartAsync(INetworkClient client, uint param)
		{
			var session = client.Session;

			// Not from the saddle. The trance ROOTS the caster (the move handler pins their position while
			// IsSpellweaving) and plays a kneeling pose, neither of which a mounted character can do - the rider would
			// be pinned mid-gallop with the horse animating underneath. Refuse the open rather than produce that.
			//
			// This stays a server-side policy: it cannot be driven off the client's own saddle gate. That gate is the
			// per-action property `GameAction_CanUseIfMounted` (stat key 0x4FD), and exactly two of the client's 260
			// GameAction types carry it - `Charge` and `ChargeSpecialAbility`, the lance charge
			// (docs/re/gameactionlist-grammar.md). No general "usable while mounted" property exists, so the client
			// asks the question only of a charge and never of a cast. Don't go looking for data behind this check.
			//
			// The refusal is RETAIL'S OWN, not our English line (#744): a ChatTextIIR carrying LDB 100/562
			// `'%s must not have %s running.'` with "You" and the mount buff substituted in. Note what that phrasing
			// settles - retail states the gate against an ACTIVE BUFF, so the check below is the right shape (the
			// mount is a buff) even though we ask it of the session.
			if (_out.IsMounted(client))
			{
				_logger.LogInformation("Spellweave START refused for '{Name}': mounted.", session.Character.Name);
				await _out.SendMountedRefusal(client);
				return;
			}

			var profile = SpellweaveCatalog.ForClass(session.Character.Class);
			if (profile == null)
			{
				// A caster class we don't have a spellweaving profile for yet (all but Necromancer). Don't open a
				// trance we can't answer - the client would stall. Report it instead.
				_logger.LogInformation("Spellweave START ignored for '{Name}': no profile for class {Class}.", session.Character.Name, session.Character.Class);
				await Feedback(client, "Spellweaving is not available for your class yet.");
				return;
			}

			session.IsSpellweaving = true;
			session.WeaveComponents.Clear();
			session.SpellweaveBuffStack = 0;
			var caster = session.Character.Id;

			// Ensure the persistent energy loop is running (inits the pool + regens it between weaves) so the weave
			// drains from the CURRENT tracked value, not a stale 0 left by a previous weave.
			_out.EnsureEnergyLoop(client);
			_logger.LogInformation("Spellweave START for '{Name}' (class={Class}, param=0x{Param:X}).", session.Character.Name, session.Character.Class, param);

			// The trance-open burst (ApplySpell). Their real durations are stamped from Spells.DurationMs by the
			// ApplySpell helper, which is what makes this byte-exact against the capture.
			//
			// ⭐ The capture's THIRD buff is not a third trance buff (#141). The NecroL80 activate carries
			// 0x37F29800, 0x37E5A800 and 0x3969D000, and the first two are the trance and `Spellweaving (1)`.
			// The third is Arcane Drought, which the client's own data shows is one of SIX outcomes of the rung-1
			// backlash roll - it sits in band 26-37 of that rung's partition. It was hardcoded here as a fixed
			// trance buff, which made every weave open with the same debuff and none of the other five ever
			// possible. Rolling it is what retail does; landing on Arcane Drought is what that capture happened
			// to see.
			await _out.ApplyBuff(client, caster, caster, profile.TranceBuff << 8);
			await _out.ApplyBuff(client, caster, caster, SpellweaveCatalog.RungBuff(profile, 0) << 8);
			await RollBacklash(client, profile, caster, 0);

			// The trance-pose animation (BCC): make the character play the spellweaving stance (without it the buffs
			// render but the body stays idle).
			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveTrance(caster)));

			// The reactive element list: tells the client which weave elements it can add (the componentIndex on each
			// add indexes into this list). This is what unblocks the adds.
			await WorldIir.Send(client, CombatReactiveCodec.Build(caster, profile.WeaveElements));

			// The trance METER: stream the draining energy bar (then the health DoT) while the trance is open.
			_ = RunTranceMeter(client, caster); // fire-and-forget; it logs its own exceptions and stops itself

			await Feedback(client, "Spellweave started.");
			// The trance root is enforced in the CharDCMove handler: while IsSpellweaving it pins the caster's
			// position (rejects translation). Cleared when IsSpellweaving clears (deactivate / fire). #141 (needs in-client test).
		}
	}
}
