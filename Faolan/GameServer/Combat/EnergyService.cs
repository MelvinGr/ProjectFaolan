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
using Faolan.GameServer.Wire;

namespace Faolan.GameServer.Combat
{
	/// <summary>
	///     The per-session energy pool: the yellow bar that sprint drains, spellweaving spends, and the Exhausted buff
	///     rides the bottom of. It owns the single persistent per-client loop and the three server-owned sprint
	///     booleans.
	/// </summary>
	/// <remarks>
	///     All of its state lives on <see cref="INetworkClientSession" /> (Energy, EnergyMax, EnergyLoopActive,
	///     IsSprinting, SprintInputRequested) rather than here, so this is a stateless collaborator over
	///     per-connection state. Held by the listener's sprint input handler and reached by
	///     <see cref="SpellweaveService" /> through <see cref="ISpellweaveOutbound" />.
	/// </remarks>
	public sealed class EnergyService
	{
		private const int EnergyRegenPerTick = 600;
		private const int EnergyTickMs = 1000;

		// How many energy ticks between two CharacterStatId.Energy mirrors. Retail's own-handle cadence is a push every
		// ~3-7s while the pool is moving and nothing at all once it is back at the max, so this is the floor of that
		// range and the "did it change" guard supplies the silence (RunEnergyMirror).
		private const int EnergyMirrorTicks = 3;

		// Server-side energy mirror for sprint: the client owns the yellow bar's visual during a sprint, but the same
		// drain and regen are tracked into session.Energy silently, with no packets, so spellweaving starts from the
		// real value and the zero-crossing is known. The rate is approximate - no capture states the sprint drain.
		private const int SprintEnergyDrainPerTick = 900; // ~necro 9000 -> ~10s of continuous sprint.

		private readonly BuffService _buffs;
		private readonly ILogger _logger;

		public EnergyService(BuffService buffs, ILogger logger)
		{
			_buffs = buffs;
			_logger = logger;
		}

		/// <summary>
		///     Initialises the energy pool and starts the single persistent energy loop. Idempotent, and called on
		///     both sprint and weave start, so energy is tracked and regenerates whichever happens first.
		/// </summary>
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

		/// <summary>
		///     The single per-session energy loop: drains while sprinting, regenerates when idle, and steps aside while
		///     weaving, because the weave owns both the drain and the 0x200E stream.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Persistent - it keeps ticking for the whole session so energy recovers after a weave or sprint - and
		///         exits only on disconnect. <c>EnergyLoopActive</c> guards against a second loop.
		///     </para>
		///     <para>
		///         It also mirrors the pool to the client as <see cref="CharacterStatId.Energy" />, which is not a
		///         decoration: 1000028 is the value the client's own energy model holds (FUN_0112ad70 caches it and
		///         drives the Exhausted state off it), and it seeds the sprint bar's view. Retail streams exactly this
		///         - 1,650 records across the corpus, on StatUpdateIIR (0x96B8DC59), per entity - with a cadence of a
		///         push every 3-7 s while the pool is moving, going silent once it is back at the max (a captured run
		///         reads 14000 -> 13977 -> 14000 -> quiet, with roughly 4800-point drops for real sprints).
		///         <see cref="EnergyMirrorTicks" /> and the <c>!= mirrored</c> guard reproduce that shape.
		///     </para>
		///     <para>
		///         This is a mirror, not the visual. The client sprints and drains locally with no server traffic at
		///         all, which is why retail sends no 0x200E during a sprint, so what this restores is the server's
		///         number rather than the bar. Only the spellweave drain is server-driven, and it owns the 0x200E
		///         stream (see <see cref="SpellweaveService" />).
		///     </para>
		/// </remarks>
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

							// An empty pool ends the sprint, and the client has to be told, or it keeps rendering a
							// sprint the server has stopped draining for. Both flags ride this same zero-crossing
							// rather than a threshold of their own: `canSprint` is the refusal the client checks
							// before it even asks (00 x151 in the corpus), `isSprinting` is the state.
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
						await GameWire.SendStatUpdate(client, new CharacterStatBlock { [CharacterStatId.Energy] = session.Energy },
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

		/// <summary>
		///     One of the three server-owned sprint booleans.
		/// </summary>
		/// <remarks>
		///     All four dispatches in this family share one endpoint pair in the corpus - sender GameCharAgentProject
		///     at the session's own small instance, receiver the player's GameCharInterfaceProject - which is the pair
		///     <see cref="SendSprintBar" /> uses.
		/// </remarks>
		private static Task SendSprintFlag(INetworkClient client, GameCharInterfaceProjectMethods method, bool on)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentProjectType, 0x54), client.Endpoint(RpcInterfaces.GameCharInterfaceProjectType),
				method, SprintFlagCodec.Build(on)));
		}

		/// <summary>
		///     The sprint key changed state.
		/// </summary>
		/// <remarks>
		///     Retail answers a change of <c>sprintInput</c> rather than every frame - 2,959 changes drew 2,624
		///     <c>DispatchSetIsSprinting</c> answers over the corpus - and it does not merely echo: 14.5% of answers
		///     carry a different value from the input before them, which is the server refusing. The one justified
		///     refusal is an empty pool, which is the same zero-crossing the Exhausted buff rides. Anything finer (a
		///     cooldown, a stance, a movement-mode gate) would be invented: the corpus shows that retail refuses, not
		///     what else it refuses for.
		/// </remarks>
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
			// `isSprinting = 0`, median 820 ms later). Every captured frame carries 0 - the corpus shows the clear and
			// never the set - so only the clear is sent.
			// [V0: what sets regenDelayed - no capture carries a 1]
			if (!granted)
				await SendSprintFlag(client, GameCharInterfaceProjectMethods.DispatchSyncRegenDelayed, false);
		}
	}
}
