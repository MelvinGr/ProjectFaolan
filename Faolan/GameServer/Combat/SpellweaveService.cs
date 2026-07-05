using System;
using System.Threading.Tasks;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The spellweaving state machine (docs/combos.md). Spellweaving is the caster analogue of the melee combo:
	// the player ACTIVATES the weave (entering a trance), ADDS components one at a time while in the trance, then
	// DEACTIVATES to fire the accumulated blend. Decoded wire (Captures/decode_spellweave.py):
	//   - activate/deactivate = c2s PerformSpecialAction (0x205A), a TOGGLE (body [u32 0x000000fc]);
	//   - add-component       = c2s CombatReactiveIIR_c (0x54C02089), body carrying the componentIndex;
	//   - fire                = s2c 0xF98E10B3 burst on deactivate; the 0xE9F24184 meter drains during the trance.
	//
	// On ACTIVATE the server must answer with the trance-open burst or the client never enters the interactive
	// trance (it stalls at "started" and never sends the adds): 3 trance buffs (ApplySpell) + the reactive element
	// LIST (which declares the addable weave elements the componentIndex indexes into). This service emits that burst
	// via two narrow seams the listener passes in - `_applySpell` (the listener's ApplySpell self-buff helper) and
	// `_sendIir` (wrap a record in the world-framed IIR carrier). The per-add element re-send (with the used element
	// zeroed) + the meter drain-stream + the componentIndex -> stat/effect contribution table are not built yet (see
	// docs/combos-spellweaving-handoff.md); the trance immobilisation is likewise a TODO.
	public sealed class SpellweaveService
	{
		private readonly Func<INetworkClient, string, Task> _feedback;
		private readonly Func<INetworkClient, uint, uint, uint, Task> _applySpell; // ApplySpell(client, applyTo, applyFrom, spellId)
		private readonly Func<INetworkClient, byte[], Task> _sendIir; // send a record on the world-framed IIR carrier
		private readonly ILogger _logger;

		// Trance-meter drain (capture-approximate, frame 1654+: the "Spellweaving Cost" gauge starts ~0x22CE and drains
		// ~119 every ~1.5s). Streamed on a self-rescheduling timer that stops when the trance closes.
		private const uint TranceMeterStartToken = 0x22CE;
		private const uint TranceMeterDrainPerTick = 119;
		private const int TranceMeterIntervalMs = 1500;
		// How long after a pick the component picker stays hidden before the server re-sends the reactive list to bring
		// it back (capture: add#0 105.8s -> list re-send 114.0s, ~8s).
		private const int ElementCooldownMs = 8000;

		public SpellweaveService(
			ILogger logger,
			Func<INetworkClient, string, Task> feedback,
			Func<INetworkClient, uint, uint, uint, Task> applySpell = null,
			Func<INetworkClient, byte[], Task> sendIir = null)
		{
			_logger = logger;
			_feedback = feedback;
			_applySpell = applySpell;
			_sendIir = sendIir;
		}

		// The activate/deactivate toggle (PerformSpecialAction). Opens the trance on the first press, fires + closes
		// it on the second. `param` is the toggle's body word (0xfc in the capture) - kept for the log / future use.
		public async Task ToggleAsync(INetworkClient client, uint param)
		{
			var session = client.Session;
			if (session?.Character == null || session.Account == null)
				return;

			if (!session.IsSpellweaving)
			{
				await StartAsync(client, param);
				return;
			}

			await FireAsync(client);
		}

		// Open the trance: resolve the caster's class profile and emit the activate-response burst (trance buffs +
		// reactive element list) so the client enters the interactive trance and can send its adds.
		private async Task StartAsync(INetworkClient client, uint param)
		{
			var session = client.Session;
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
			var caster = session.Account.ClientInstance;
			_logger.LogInformation("Spellweave START for '{Name}' (class={Class}, param=0x{Param:X}).", session.Character.Name, session.Character.Class, param);

			// The three trance buffs (ApplySpell): open the trance state client-side. Their real durations are stamped
			// from Spells.DurationMs by the ApplySpell helper (matches the capture byte-for-byte).
			if (_applySpell != null)
				foreach (var buff in profile.TranceBuffs)
					await _applySpell(client, caster, caster, buff);

			// The trance-pose animation (BCC): make the character play the spellweaving stance (without it the buffs
			// render but the body stays idle).
			if (_sendIir != null)
				await _sendIir(client, BehaviorAnimationCodec.SpellweaveTrance(caster));

			// The reactive element list: tells the client which weave elements it can add (the componentIndex on each
			// add indexes into this list). This is what unblocks the adds.
			if (_sendIir != null)
				await _sendIir(client, SpellweaveCodec.BuildReactiveList(caster, profile.WeaveElements));

			// The trance METER: stream the draining "Spellweaving Cost" gauge on a timer (the visible trance timer).
			StartTranceMeter(client, caster);

			await Feedback(client, "Spellweave started.");
			// TODO: immobilise the caster for the trance (the move handler should reject motion while IsSpellweaving).
		}

		// Streams the trance meter (0xE9F24184, "Spellweaving Cost") on a self-rescheduling timer while the trance is
		// open, draining the gauge each tick. Stops as soon as IsSpellweaving clears (deactivate / logout). The
		// drain curve is capture-approximate; the packet itself is byte-exact (SpellweaveCodec.BuildTranceMeter).
		private void StartTranceMeter(INetworkClient client, uint caster)
		{
			var token = TranceMeterStartToken;

			void Tick()
			{
				Task.RunAfter(TranceMeterIntervalMs, async () =>
				{
					var session = client.Session;
					if (session?.Account == null || !session.IsSpellweaving)
						return; // trance closed -> stop the loop

					var drain = Math.Min(TranceMeterDrainPerTick, token);
					token -= drain;
					if (_sendIir != null)
						await _sendIir(client, SpellweaveCodec.BuildTranceMeter(caster, token, -(int)drain));
					Tick();
				}, _logger);
			}

			Tick();
		}

		// A component add (CombatReactiveIIR) - only meaningful inside an open trance. componentIndex indexes into the
		// caster's weave-element list. Retail answers each add with a burst (Captures/decode_spellweave.py, frames
		// 1657-1661): the component's OWN spell applied immediately as a self-buff (necro: add idx0->Arcane Renewal
		// 0x396AE9, idx1->Arcane Surge 0x3969D8, idx3->Death God 0x396A22 - so necro components take effect as you
		// weave them), then a re-sent element list. Without that response the client only ever registers the FIRST
		// pick (it never gets the ack that re-arms it). The animation BCC pair in that burst is not built yet.
		public async Task AddComponentAsync(INetworkClient client, uint componentIndex)
		{
			var session = client.Session;
			if (session?.Character == null || session.Account == null)
				return;

			if (!session.IsSpellweaving)
			{
				_logger.LogInformation("Spellweave component #{Idx} ignored - no open weave for '{Name}'.", componentIndex, session.Character.Name);
				return;
			}

			var profile = SpellweaveCatalog.ForClass(session.Character.Class);
			var caster = session.Account.ClientInstance;
			session.WeaveComponents.Add(componentIndex);
			_logger.LogInformation("Spellweave ADD component #{Idx} (#{Count}) for '{Name}'.", componentIndex, session.WeaveComponents.Count, session.Character.Name);

			var hasElement = profile != null && componentIndex < profile.WeaveElements.Length;

			// The component-add gesture animation (BCC begin + complete), carrying the added element.
			if (hasElement && _sendIir != null)
			{
				await _sendIir(client, BehaviorAnimationCodec.SpellweaveComponentAdd(caster, profile.WeaveElements[componentIndex], complete: false));
				await _sendIir(client, BehaviorAnimationCodec.SpellweaveComponentAdd(caster, profile.WeaveElements[componentIndex], complete: true));
			}

			// Immediate component effect: apply the element's own spell as a self-buff (the full id is the base << 8).
			if (hasElement && _applySpell != null)
				await _applySpell(client, caster, caster, profile.WeaveElements[componentIndex] << 8);

			// Do NOT re-send the reactive list now: the client HIDES the component picker itself once a component is
			// picked (the capture sends no list after an add - the next one is frame 1705, ~8s later). Re-sending it
			// immediately is what kept the picker up and let the player spam. Instead bring the picker back after the
			// cooldown by re-sending the full list, so the flow is: pick -> picker disappears -> ~cooldown -> reappears.
			if (profile != null && _sendIir != null)
				Task.RunAfter(ElementCooldownMs, async () =>
				{
					var session = client.Session;
					if (session?.Account != null && session.IsSpellweaving)
						await _sendIir(client, SpellweaveCodec.BuildReactiveList(caster, profile.WeaveElements));
				}, _logger);

			await Feedback(client, $"Weave component added ({session.WeaveComponents.Count}).");
		}

		// Deactivate: fire the accumulated weave and close the trance. Emits the s2c fire burst (0xF98E10B3); the
		// componentIndex -> effect table + backlash roll await the weave-element contribution table.
		private async Task FireAsync(INetworkClient client)
		{
			var session = client.Session;
			var profile = SpellweaveCatalog.ForClass(session.Character.Class);
			var caster = session.Account.ClientInstance;
			var components = string.Join(",", session.WeaveComponents);
			_logger.LogInformation("Spellweave FIRE for '{Name}': {Count} components [{Components}].", session.Character.Name, session.WeaveComponents.Count, components);

			if (profile != null && _sendIir != null)
				foreach (var stage in profile.FireStages)
					await _sendIir(client, SpellweaveCodec.BuildFireStage(caster, stage));

			await Feedback(client, $"Spellweave fired with {session.WeaveComponents.Count} components.");

			// TODO: resolve the blended effect from the accumulated components (componentIndex -> effect), roll
			// backlash, and release the trance immobilisation.
			session.IsSpellweaving = false;
			session.WeaveComponents.Clear();
		}

		private Task Feedback(INetworkClient client, string message)
		{
			return _feedback != null
				? _feedback(client, message)
				: Task.CompletedTask;
		}
	}
}
