using System;
using System.Threading.Tasks;
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

			// The reactive element list: tells the client which weave elements it can add (the componentIndex on each
			// add indexes into this list). This is what unblocks the adds.
			if (_sendIir != null)
				await _sendIir(client, SpellweaveCodec.BuildReactiveList(caster, profile.WeaveElements));

			await Feedback(client, "Spellweave started.");
			// TODO: immobilise the caster for the trance (the move handler should reject motion while IsSpellweaving)
			// and stream the 0xE9F24184 trance meter (the draining gauge) - see docs/combos-spellweaving-handoff.md.
		}

		// A component add (CombatReactiveIIR) - only meaningful inside an open trance. Accumulates the index (which
		// indexes into the caster's weave-element list).
		public async Task AddComponentAsync(INetworkClient client, uint componentIndex)
		{
			var session = client.Session;
			if (session?.Character == null)
				return;

			if (!session.IsSpellweaving)
			{
				_logger.LogInformation("Spellweave component 0x{Idx:X} ignored - no open weave for '{Name}'.", componentIndex, session.Character.Name);
				return;
			}

			session.WeaveComponents.Add(componentIndex);
			_logger.LogInformation("Spellweave ADD component #{Idx} (#{Count}) for '{Name}'.", componentIndex, session.WeaveComponents.Count, session.Character.Name);
			await Feedback(client, $"Weave component added ({session.WeaveComponents.Count}).");
			// TODO: re-send the reactive element list with the used element zeroed (the per-element cooldown), and
			// apply the component's stat/effect contribution (needs the componentIndex -> effect table).
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
