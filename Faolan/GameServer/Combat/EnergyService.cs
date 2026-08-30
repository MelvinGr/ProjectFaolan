using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Combat
{
	// The per-session ENERGY pool: the yellow bar that sprint drains, spellweaving spends, and the Exhausted buff
	// rides the bottom of. It owns the single persistent per-client loop and the three server-owned sprint booleans.
	//
	// All of its state lives on INetworkClientSession (Energy / EnergyMax / EnergyLoopActive / IsSprinting /
	// SprintInputRequested), NOT here - so this is a stateless collaborator over per-connection state, which is what
	// let it leave the listener. Held by the listener (the sprint input handler) and reached by SpellweaveService
	// through ISpellweaveOutbound.
	public sealed class EnergyService
	{
		private const int EnergyRegenPerTick = 600;
		private const int EnergyTickMs = 1000;

		// How many energy ticks between two CharacterStatId.Energy mirrors. Retail's own-handle cadence is a push every
		// ~3-7s while the pool is moving and nothing at all once it is back at the max, so this is the floor of that
		// range and the "did it change" guard supplies the silence (RunEnergyMirror).
		private const int EnergyMirrorTicks = 3;

		// Server-side ENERGY mirror for sprint: the client owns the yellow bar's visual during sprint, but we track the
		// same drain/regen into session.Energy (silently, no packets) so spellweaving starts from the real value and we
		// know when it hits 0. Rates are approximate (no capture shows the exact sprint drain/regen).
		private const int SprintEnergyDrainPerTick = 900; // ~necro 9000 -> ~10s of continuous sprint.

		private readonly BuffService _buffs;
		private readonly ILogger _logger;

		public EnergyService(BuffService buffs, ILogger logger)
		{
			_buffs = buffs;
			_logger = logger;
		}

		// Inits the energy pool + starts the single persistent energy loop (idempotent). Called on sprint and on weave
		// start, so energy is tracked + regenerates regardless of which happens first.
		public void EnsureEnergyLoop(INetworkClient client)
		{
			var session = client.Session;
			if (session.Character == null)
				return;

			if (session.EnergyMax == 0)
			{
				session.EnergyMax = SpellweaveCatalog.EnergyMaxForClass(session.Character.Class);
				session.Energy = session.EnergyMax;
			}

			if (!session.EnergyLoopActive)
				_ = RunEnergyMirror(client); // fire-and-forget; it logs its own exceptions
		}

		// The single per-session energy loop: drains while sprinting, REGENS when idle, and steps aside while weaving
		// (the weave owns the drain + the 0x200E stream). Persistent - it keeps ticking for the whole session so energy
		// recovers after a weave/sprint - and exits only on disconnect. A PeriodicTimer loop, not a self-rescheduling
		// callback. EnergyLoopActive guards against a second loop.
		//
		// It also MIRRORS the pool to the client as CharacterStatId.Energy, which is not a decoration: 1000028 is the
		// value the client's own energy model holds (FUN_0112ad70 caches it and drives the Exhausted state off it), and
		// it is what the sprint bar's view is seeded from. Retail streams exactly this - 1,650 records across the
		// corpus, on StatUpdateIIR (0x96B8DC59), per entity - and its cadence is a push every ~3-7s WHILE the pool is
		// moving, going silent once it is back at the max (Hoxxling_..._gameplay2: 14000 -> 13977 -> 14000 -> quiet,
		// with ~4800-point drops for real sprints). EnergyMirrorTicks + the `!= mirrored` guard reproduce that shape:
		// the loop is silent at full and speaks only while the number is changing.
		//
		// ⚠️ This is a MIRROR, not the visual. The client sprints and drains locally with no server traffic at all -
		// which is why retail sends no 0x200E during a sprint - so what this restores is the server's number, not the
		// bar. Only the SPELLWEAVE drain is server-driven, and that owns the 0x200E stream (see SpellweaveService).
		private async Task RunEnergyMirror(INetworkClient client)
		{
			client.Session.EnergyLoopActive = true;
			try
			{
				// What was last put on the wire as CharacterStatId.Energy, so the mirror only speaks when the pool
				// actually moved. uint.MaxValue is "nothing sent yet" rather than 0, which is a legal energy value.
				var mirrored = uint.MaxValue;
				var sinceMirror = 0;
				var exhausted = false;

				using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(EnergyTickMs));
				while (await timer.WaitForNextTickAsync())
				{
					var session = client.Session;
					if (session.Account == null)
						return; // disconnected

					if (session.IsSpellweaving)
						continue; // the weave owns the energy this tick

					if (session.IsSprinting)
						session.Energy -= (uint)Math.Min(SprintEnergyDrainPerTick, (int)session.Energy);
					else if (session.Energy < session.EnergyMax)
						session.Energy = Math.Min(session.EnergyMax, session.Energy + EnergyRegenPerTick);

					// Exhausted rides the pool, not the weave: retail applies it the instant Energy reaches 0 and sends
					// the end twin on the first push that lifts it back above zero, sprint included
					// (SpellweaveCatalog.ExhaustedBuff carries the capture). The weave has its own copy of this because
					// it also owns a health DoT while empty; this is the plain-sprint half.
					if (session.Character is { } self)
					{
						if (session.Energy == 0 && !exhausted)
						{
							exhausted = true;
							await _buffs.ApplyBuffById(client, self.Id, self.Id, SpellweaveCatalog.ExhaustedBuff);

							// An empty pool ends the sprint, and the client has to be TOLD - otherwise it keeps
							// rendering a sprint the server has stopped draining for (#867). Both flags ride this
							// same zero-crossing rather than a threshold of their own: `canSprint` is the refusal the
							// client checks before it even asks (00 x151 in the corpus), `isSprinting` is the state.
							if (session.IsSprinting)
							{
								session.IsSprinting = false;
								await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSetIsSprinting, false);
							}

							await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSyncCanSprint, false);
						}
						else if (session.Energy > 0 && exhausted)
						{
							exhausted = false;
							await _buffs.SendBuffRemoval(client, self.Id, self.Id, SpellweaveCatalog.ExhaustedBuff);
							await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSyncCanSprint, true);
						}
					}

					if (++sinceMirror >= EnergyMirrorTicks && session.Energy != mirrored && session.Character != null)
					{
						sinceMirror = 0;
						mirrored = session.Energy;
						await GameServerListener.SendStatUpdate(client, new CharacterStatBlock { [CharacterStatId.Energy] = session.Energy },
							session.Character.Id);
					}
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Energy mirror loop failed.");
			}
			finally
			{
				var session = client.Session;
				session.EnergyLoopActive = false;
			}
		}

		public static Task SendSprintBar(INetworkClient client, uint energy)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x54), client.Endpoint(RpcInterfaces.GameCharInterfaceProjectType),
				GameCharInterfaceProjectMethods.DispatchUpdateSprintbar, SprintBarCodec.Build(energy)));
		}

		// One of the three server-owned sprint booleans (#867). All four dispatches in this family share one endpoint
		// pair in the corpus - sender GameCharAgentProject at the session's own small instance, receiver the player's
		// GameCharInterfaceProject - which is the pair SendSprintBar already used.
		private static Task SendSprintFlag(INetworkClient client, GameCharInterfaceProjectMethods method, bool on)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x54), client.Endpoint(RpcInterfaces.GameCharInterfaceProjectType),
				method, SprintFlagCodec.Build(on)));
		}

		// The sprint key changed state. Retail answers a CHANGE of `sprintInput` rather than every frame - 2,959
		// changes drew 2,624 `DispatchSetIsSprinting` answers over the corpus - and it does NOT merely echo: 14.5% of
		// answers carry a different value from the input before them, which is the server refusing.
		//
		// The one refusal we can justify is an empty pool, and it needs no new rule: it is the same zero-crossing the
		// Exhausted buff already rides below. Anything finer (a cooldown, a stance, a movement-mode gate) would be
		// invented - the corpus shows THAT retail refuses, not what else it refuses for.
		public async Task TrackSprintEnergy(INetworkClient client, bool sprinting)
		{
			var session = client.Session;
			EnsureEnergyLoop(client);

			if (session.SprintInputRequested == sprinting)
				return;

			session.SprintInputRequested = sprinting;

			var granted = sprinting && session.Energy > 0;
			session.IsSprinting = granted;
			await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSetIsSprinting, granted);

			// A sprint ending is where retail puts most of its regen-delayed sends (514 of them follow an
			// `isSprinting = 0`, median 820 ms later). Every captured frame carries 0 - the corpus proves the CLEAR
			// and never the set, so this sends only what it can show.
			// `[V0: what sets regenDelayed - no capture carries a 1]`
			if (!granted)
				await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSyncRegenDelayed, false);
		}
	}
}
