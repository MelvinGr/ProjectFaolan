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
	/// <summary>
	///     The spellweaving state machine (docs/subsystems/combos.md): the caster analogue of the melee combo. The
	///     player activates the weave, entering a trance, adds components one at a time while in it, then deactivates
	///     to fire the accumulated blend.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The decoded wire (tools/capture/decode_spellweave.py) is: activate and deactivate are one c2s
	///         PerformSpecialAction (0x205A) toggle, body [u32 0x000000fc]; add-component is c2s CombatReactiveIIR_c
	///         (0x54C02089), body carrying the componentIndex; and the fire is the s2c 0xF98E10B3 burst on deactivate,
	///         with the 0xE9F24184 meter draining during the trance.
	///     </para>
	///     <para>
	///         On activate the server must answer with the trance-open burst - the trance buffs (ApplySpell) plus the
	///         reactive element list that declares the addable weave elements the componentIndex indexes into - or the
	///         client never enters the interactive trance: it stalls at "started" and sends no adds. This service
	///         emits that burst through <see cref="ISpellweaveOutbound" />. The trance immobilisation is wired in the
	///         CharDCMove handler, which pins the caster's position while <c>IsSpellweaving</c>.
	///     </para>
	///     <para>
	///         The per-class table this runs on is the client's own - see <see cref="SpellweaveCatalog" />. All four
	///         weaving classes have a profile, the stack is a ladder of six distinct buffs rather than a base plus an
	///         offset, and each rung rolls a backlash off a six-way partition of one draw. The rung's own stat payload
	///         is not modelled here: the ladder's <c>Modify BaseSpellDamage</c> step (250 per rung for the
	///         necromancer, 100 for the other three), its <c>Modify NaturalEnergyRegeneration -15000</c> and the
	///         <c>SpellLineStatModifier</c> splash it grants the class's spell lines are ordinary GameAction elements
	///         on the rung's own record, so they land when the generic element engine walks a CastNano chain and
	///         evaluates criteria.
	///     </para>
	/// </remarks>
	public sealed class SpellweaveService
	{
		private const int DotDrainPerTick = 31_705;

		// How long after a pick the component picker stays hidden before the server re-sends the reactive list to bring
		// it back (capture: add#0 105.8s -> list re-send 114.0s, ~8s).
		private const int ElementCooldownMs = 8000;

		// Trance drain: the yellow energy bar drains while weaving. The bar is moved by DispatchUpdateSprintbar
		// (0x200E, ISpellweaveOutbound.SendSprintBar) - full is 9000 for the necromancer - alongside the 0x37E68C
		// "Spellweaving Cost" gauge; retail sends both, under the same token. The drain accelerates as the weave goes
		// on (the capture's -90 to -270). When energy hits 0 the health DoT (0x37E690) kicks in, draining about 31705
		// health per tick, which is the damage for over-weaving. Streamed on a timer; the DoT is visual, draining the
		// health bar, so it does not move authoritative vitals.
		private const int EnergyBaseDrain = 90;
		private const int EnergyDrainAccelPerTick = 6; // -90 -> ~-270 over the weave, matching the capture curve
		private const int EnergyDrainMax = 270;
		private const int TranceMeterIntervalMs = 1500;

		private const uint DotStartHealth = 602_395; // ~necro health when the DoT began (approx; should be real health)

		// The "Exhausted" debuff applied when the energy pool hits 0. Shared with the sprint path, which retail drives
		// the same way - see SpellweaveCatalog.ExhaustedBuff for the capture that confirms the id and both twins.
		private const uint ExhaustedBuff = SpellweaveCatalog.ExhaustedBuff;
		private readonly ILogger _logger;
		private readonly ISpellweaveOutbound _out;

		public SpellweaveService(ILogger logger, ISpellweaveOutbound outbound)
		{
			_logger = logger;
			_out = outbound;
		}

		/// <summary>
		///     A component add (CombatReactiveIIR), only meaningful inside an open trance.
		///     <paramref name="componentIndex" /> indexes into the caster's weave-element list.
		/// </summary>
		/// <remarks>
		///     Retail answers each add with a burst (tools/capture/decode_spellweave.py, frames 1657-1661): the
		///     component's own spell applied immediately as a self-buff - for the necromancer, idx0 is Arcane Renewal
		///     0x396AE9, idx1 Arcane Surge 0x3969D8 and idx3 Death God 0x396A22, so its components take effect as they
		///     are woven - then a re-sent element list. Without that response the client registers only the first pick,
		///     because it never gets the ack that re-arms it.
		/// </remarks>
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

			// Do not re-send the reactive list now: the client hides the component picker itself once a component is
			// picked, and the capture sends no list after an add - the next one is frame 1705, about 8 s later.
			// Re-sending immediately keeps the picker up and lets the player spam it. Instead bring the picker back
			// after the cooldown by re-sending the full list: pick, picker disappears, cooldown, reappears.
			if (profile != null)
			{
				Task.RunAfter(ElementCooldownMs, async () =>
				{
					var session = client.Session;
					if (session.Account == null || !session.IsSpellweaving)
						return;

					// Advance the weave one rung - `Spellweaving (1)` to `(2)` and so on. The new buff overlaps the
					// previous one still ticking down, which is the brief doubled icon the player sees. Each rung is
					// a distinct buff off the class ladder rather than the same buff at an incremented id: only the
					// necromancer's six happen to be contiguous, and two of the four classes would walk into
					// unrelated spells under arithmetic (see SpellweaveCatalog.StackLadder).
					session.SpellweaveBuffStack++;
					await _out.ApplyBuff(client, caster, caster, SpellweaveCatalog.RungBuff(profile, session.SpellweaveBuffStack) << 8);

					// ... and roll this rung's backlash. The bands widen as the stack climbs, so the sixth component
					// cannot escape one.
					await RollBacklash(client, profile, caster, session.SpellweaveBuffStack);

					await WorldIir.Send(client, CombatReactiveCodec.Build(caster, profile.WeaveElements));
				}, _logger);
			}

			await Feedback(client, $"Weave component added ({session.WeaveComponents.Count}).");
		}

		/// <summary>
		///     The activate/deactivate toggle (PerformSpecialAction): opens the trance on the first press, fires and
		///     closes it on the second.
		/// </summary>
		/// <param name="param">The toggle's body word, 0xfc in the capture. Logged rather than acted on.</param>
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

		/// <summary>
		///     Rolls the rung's backlash and applies the outcome, or nothing when the roll escapes.
		/// </summary>
		/// <remarks>
		///     The table is the client's own (<c>SpellweaveCatalog.RollBacklash</c>): one draw of 0..99 partitioned six
		///     ways, so at most one backlash can land per rung. The randomness lives here rather than in the catalog so
		///     the partition itself stays a pure function a test can walk end to end.
		/// </remarks>
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

		/// <summary>
		///     Deactivate: close the trance.
		/// </summary>
		/// <remarks>
		///     There is no separate fire payload to send. The weave's whole reward is the <c>Spellweaving (N)</c> rung
		///     buff it leaves running, and that buff's duration is the reward window - 12 s at rung 1, rising to 32 s
		///     at rung 5 - so deactivating ends the trance and lets the rung tick down.
		/// </remarks>
		private async Task FireAsync(INetworkClient client)
		{
			var session = client.Session;
			var profile = SpellweaveCatalog.ForClass(session.Character.Class);
			var caster = session.Character.Id;
			var components = string.Join(",", session.WeaveComponents);
			_logger.LogInformation("Spellweave FIRE for '{Name}': {Count} components [{Components}].", session.Character.Name, session.WeaveComponents.Count, components);

			// Clear IsSpellweaving first so the meter and add loops stop before the trance is torn down.
			session.IsSpellweaving = false;

			// A weave pushes no stat records here. The four StatIIR records around a captured fire are the level-scaled
			// rating quad (AttackRating / MagicRating / DefenseRating / MagicalResist, CombatRatingCurve's
			// `400 x level - 300`), not the weave's: they appear in 31 captures, 20 of which contain no spellweaving at
			// all - including a Herald of Xotli, which cannot weave.

			// The trance-exit animation: take the character out of the weave pose, or it stays stuck kneeling.
			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveTranceExit(caster)));

			// Close the trance: remove the trance buffs (their rank-1 end-twins) so the client clears the icons and
			// exits the trance pose - in capture, the cancel press removes 37F298 plus the current 37E5A8 stack.
			// Without this the client stays in the trance visually even though the server considers the weave done.
			if (profile != null)
			{
				await _out.RemoveBuff(client, caster, caster, profile.TranceBuff << 8);
				await _out.RemoveBuff(client, caster, caster, SpellweaveCatalog.RungBuff(profile, session.SpellweaveBuffStack) << 8);

				// Two removals, not three: Arcane Drought is not a trance buff but a backlash roll outcome with a
				// 10 s duration of its own, and the capture's cancel press removes exactly 0x37F298 and the current
				// rung. Ending a backlash early because the player cancelled is the opposite of what it is for.
			}

			await Feedback(client, $"Spellweave fired with {session.WeaveComponents.Count} components.");

			// The trance root is already released: IsSpellweaving was cleared above, so the move handler stops
			// pinning the caster.
			session.WeaveComponents.Clear();
			session.SpellweaveBuffStack = 0;
		}

		/// <summary>
		///     Streams the trance drain while the trance is open, stopping as soon as <c>IsSpellweaving</c> clears on
		///     deactivate or logout.
		/// </summary>
		/// <remarks>
		///     Phase 1 drains the yellow energy bar from the current tracked value at an accelerating rate; phase 2,
		///     once energy is empty, applies the Exhausted debuff and the health DoT gauge - the damage for
		///     over-weaving. The packets are byte-exact; the curves are approximate.
		/// </remarks>
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
						// Phase 1: energy drains from the current tracked value (not always full), accelerating as the
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
						// Energy just hit 0: apply the Exhausted depletion debuff once.
						if (!exhausted)
						{
							exhausted = true;
							await _out.ApplyBuff(client, caster, caster, ExhaustedBuff);
						}

						// Phase 2: energy empty, so the health DoT runs - the damage for over-weaving.
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

		/// <summary>
		///     Opens the trance: resolves the caster's class profile and emits the activate-response burst - trance
		///     buffs plus the reactive element list - so the client enters the interactive trance and can send its
		///     adds.
		/// </summary>
		private async Task StartAsync(INetworkClient client, uint param)
		{
			var session = client.Session;

			// Not from the saddle. The trance roots the caster (the move handler pins their position while
			// IsSpellweaving) and plays a kneeling pose, neither of which a mounted character can do - the rider would
			// be pinned mid-gallop with the horse animating underneath. Refuse the open rather than produce that.
			//
			// This stays a server-side policy: it cannot be driven off the client's own saddle gate. That gate is the
			// per-action property `GameAction_CanUseIfMounted` (stat key 0x4FD), and exactly two of the client's 260
			// GameAction types carry it - `Charge` and `ChargeSpecialAbility`, the lance charge
			// (docs/re/gameactionlist-grammar.md). No general "usable while mounted" property exists, so the client
			// asks the question only of a charge and never of a cast. Don't go looking for data behind this check.
			//
			// The refusal is retail's own rather than a composed English line: a ChatTextIIR carrying LDB 100/562
			// `'%s must not have %s running.'` with "You" and the mount buff substituted in. That phrasing also
			// settles the shape of the check - retail states the gate against an active buff, and the mount is a
			// buff, even though the question is asked of the session here.
			if (_out.IsMounted(client))
			{
				_logger.LogInformation("Spellweave START refused for '{Name}': mounted.", session.Character.Name);
				await _out.SendMountedRefusal(client);
				return;
			}

			var profile = SpellweaveCatalog.ForClass(session.Character.Class);
			if (profile == null)
			{
				// A caster class with no spellweaving profile. Do not open a trance that cannot be answered - the
				// client would stall - and report it instead.
				_logger.LogInformation("Spellweave START ignored for '{Name}': no profile for class {Class}.", session.Character.Name, session.Character.Class);
				await Feedback(client, "Spellweaving is not available for your class yet.");
				return;
			}

			session.IsSpellweaving = true;
			session.WeaveComponents.Clear();
			session.SpellweaveBuffStack = 0;
			var caster = session.Character.Id;

			// Ensure the persistent energy loop is running (inits the pool + regens it between weaves) so the weave
			// drains from the current tracked value, not a stale 0 left by a previous weave.
			_out.EnsureEnergyLoop(client);
			_logger.LogInformation("Spellweave START for '{Name}' (class={Class}, param=0x{Param:X}).", session.Character.Name, session.Character.Class, param);

			// The trance-open burst (ApplySpell). Their real durations are stamped from Spells.DurationMs by the
			// ApplySpell helper, which is what makes this byte-exact against the capture.
			//
			// The capture's third buff is not a third trance buff. The NecroL80 activate carries 0x37F29800,
			// 0x37E5A800 and 0x3969D000: the first two are the trance and `Spellweaving (1)`, and the third is
			// Arcane Drought, which the client's own data shows is one of six outcomes of the rung-1 backlash roll,
			// sitting in band 26-37 of that rung's partition. Rolling it is what retail does; landing on Arcane
			// Drought is what that capture happened to see.
			await _out.ApplyBuff(client, caster, caster, profile.TranceBuff << 8);
			await _out.ApplyBuff(client, caster, caster, SpellweaveCatalog.RungBuff(profile, 0) << 8);
			await RollBacklash(client, profile, caster, 0);

			// The trance-pose animation (BCC): make the character play the spellweaving stance (without it the buffs
			// render but the body stays idle).
			await WorldIir.Send(client, BehaviorControlCenterCodec.Build(BehaviorAnimations.SpellweaveTrance(caster)));

			// The reactive element list: tells the client which weave elements it can add (the componentIndex on each
			// add indexes into this list). This is what unblocks the adds.
			await WorldIir.Send(client, CombatReactiveCodec.Build(caster, profile.WeaveElements));

			// The trance meter: stream the draining energy bar (then the health DoT) while the trance is open.
			_ = RunTranceMeter(client, caster); // fire-and-forget; it logs its own exceptions and stops itself

			await Feedback(client, "Spellweave started.");
			// The trance root is enforced in the CharDCMove handler: while IsSpellweaving it pins the caster's
			// position, rejecting translation. Released when IsSpellweaving clears on deactivate or fire.
		}
	}
}
