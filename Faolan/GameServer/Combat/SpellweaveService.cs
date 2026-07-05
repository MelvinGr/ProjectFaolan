using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The spellweaving state machine (#, docs/combos.md). Spellweaving is the caster analogue of the melee combo:
	// the player ACTIVATES the weave (entering a trance), ADDS components one at a time while in the trance, then
	// DEACTIVATES to fire the accumulated blend. Decoded wire:
	//   - activate/deactivate = c2s PerformSpecialAction (0x205A), a TOGGLE (body [u32 0x000000fc]);
	//   - add-component       = c2s CombatReactiveIIR_c (0x54C02089), body carrying the componentIndex;
	//   - fire                = s2c 0xF98E10B3 burst on deactivate; the 0xE9F24184 meter drains during the trance.
	// This service owns the toggle/accumulate/fire transitions over the per-session weave state
	// (Session.IsSpellweaving + Session.WeaveComponents). The componentIndex -> stat/effect contribution table and
	// the exact fire packet + backlash roll are NOT decoded yet, so Fire currently REPORTS the completed weave
	// (feedback + log) rather than emitting an effect - wiring those in is the remaining step once the weave-element
	// table is recovered. Immobilisation during the trance is likewise a TODO (needs the move handler to consult
	// IsSpellweaving).
	public sealed class SpellweaveService
	{
		private readonly Func<INetworkClient, string, Task> _feedback;
		private readonly ILogger _logger;

		public SpellweaveService(ILogger logger, Func<INetworkClient, string, Task> feedback)
		{
			_logger = logger;
			_feedback = feedback;
		}

		// The activate/deactivate toggle (PerformSpecialAction). Opens the trance on the first press, fires + closes
		// it on the second. `param` is the toggle's body word (0xfc in the capture) - kept for the log / future use.
		public async Task ToggleAsync(INetworkClient client, uint param)
		{
			var session = client.Session;
			if (session?.Character == null)
				return;

			if (!session.IsSpellweaving)
			{
				session.IsSpellweaving = true;
				session.WeaveComponents.Clear();
				_logger.LogInformation("Spellweave START for '{Name}' (param=0x{Param:X}).", session.Character.Name, param);
				await Feedback(client, "Spellweave started.");
				// TODO: immobilise the caster for the trance (the move handler should reject motion while IsSpellweaving).
				return;
			}

			await FireAsync(client);
		}

		// A component add (CombatReactiveIIR) - only meaningful inside an open trance. Accumulates the index.
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
			_logger.LogInformation("Spellweave ADD component 0x{Idx:X} (#{Count}) for '{Name}'.", componentIndex, session.WeaveComponents.Count, session.Character.Name);
			await Feedback(client, $"Weave component added ({session.WeaveComponents.Count}).");
		}

		// Deactivate: fire the accumulated weave and close the trance. The actual s2c effect (0xF98E10B3) + backlash
		// await the componentIndex -> effect table; for now we report the completed weave so the flow is verifiable.
		private async Task FireAsync(INetworkClient client)
		{
			var session = client.Session;
			var components = string.Join(",", session.WeaveComponents);
			_logger.LogInformation("Spellweave FIRE for '{Name}': {Count} components [{Components}].", session.Character.Name, session.WeaveComponents.Count, components);
			await Feedback(client, $"Spellweave fired with {session.WeaveComponents.Count} components.");

			// TODO: build + send the 0xF98E10B3 fire effect from the accumulated components (componentIndex -> effect),
			// roll backlash, and release the trance immobilisation.
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
