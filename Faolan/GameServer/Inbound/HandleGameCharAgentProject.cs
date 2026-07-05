using System;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.Combat;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private const int EnergyRegenPerTick = 600;
		private const int EnergyTickMs = 1000;

		// Server-side ENERGY mirror for sprint: the client owns the yellow bar's visual during sprint, but we track the
		// same drain/regen into session.Energy (silently, no packets) so spellweaving starts from the real value and we
		// know when it hits 0. Rates are approximate (no capture shows the exact sprint drain/regen).
		private const int SprintEnergyDrainPerTick = 900; // ~necro 8910 -> ~10s of continuous sprint

		private static readonly byte[] SprintBarSender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentProjectType, 0x54);

		// Inits the energy pool + starts the single persistent energy loop (idempotent). Called on sprint and on weave
		// start, so energy is tracked + regenerates regardless of which happens first.
		internal void EnsureEnergyLoop(INetworkClient client)
		{
			var session = client.Session;
			if (session?.Character == null)
				return;

			if (session.EnergyMax == 0)
			{
				session.EnergyMax = SpellweaveCatalog.EnergyMaxForClass(session.Character.Class);
				session.Energy = session.EnergyMax;
			}

			if (!session.EnergyLoopActive)
				_ = RunEnergyMirror(client); // fire-and-forget; it logs its own exceptions
		}

		// Inbound RPCs on the GameCharAgentProject endpoint (0x5B9756FC). Routed here by class id from
		// ReceivedPacket because its method ids (0x2000, ...) collide with ordinary GameServer opcodes.
		//
		// The one that matters for world entry is RequestAggroStatus (client_to_server[0] => opcode 0x2000): once
		// the playfield is built the client polls the server for the aggro state between the player and each nearby
		// hostile NPC, and will NOT finish the loading screen until each poll is answered. Retail answers every one
		// (~17 over a login); we answered none, so the live client retried the same poll 450+ times and sat on the
		// loading screen forever - which is why it never opened the AgentServer/CommunityServer (chat) connection.
		// Decoded from the kalantes_chat retail capture (port 7015):
		//   req  C->S: recv=GameCharAgentProject, send=GameCharInterfaceProject/<player>, op 0x2000,
		//              body [u32 0xC350][u32 charInstance][u32 0xC350][u32 targetInstance]   (two ProtoID32s)
		//   resp S->C: send=GameCharAgentProject/<zone handle 0x58>, recv=GameCharInterfaceProject/<player>, op 0x2000,
		//              body [u32 0xC350][u32 targetInstance][u32 cCategory]
		// The 3rd field is the target's overhead NAME-COLOUR category (0 friendly / 1 hostile / 2 neutral), the SOLE
		// driver of the red/green/yellow name (proven by live gdb, #256) - not a live-aggro flag. We answer every
		// poll (still unblocks the load) and now set 1 for hostile NPCs so their names render red like retail.
		private async Task HandleGameCharAgentProject(INetworkClient client, ConanPacket packet)
		{
			var opcode = packet.Opcode;
			if (opcode != 0x2000)
			{
				// SetSprintInputState (0x200C, the sprint key): a fire-and-forget input-state update retail does NOT
				// reply to (#72). Body = [u16 state] (1 = on, 0 = off). Sprint drains the yellow ENERGY bar CLIENT-side
				// (retail's server barely streams the bar during sprint - verified: 1 packet across a sprint-heavy
				// capture). We don't reply or drive the bar; we only MIRROR the drain into the tracked session.Energy so
				// spellweaving starts from the real current value and Exhausted can fire at 0 (docs/combos.md).
				if (opcode == 0x200C)
				{
					var state = packet.Body != null && packet.Body.Length - packet.Body.Position >= sizeof(ushort)
						? packet.Body.ReadUInt16()
						: (ushort)0;
					TrackSprintEnergy(client, state == 1);
					return;
				}

				// Other GameCharAgentProject methods are not loading gates and retail's replies aren't decoded yet -
				// log and no-op rather than misparse them as a 0x2000 ping.
				Logger.LogDebug("GameCharAgentProject opcode 0x{Opcode:X4} (unhandled, no reply).", opcode);
				return;
			}

			// RequestAggroStatus(const ID32& charId, const ID32& targetId): each ID32 is [u32 type][u32 instance].
			packet.Body.ReadUInt32(); // charId.type   (0x0000C350)
			packet.Body.ReadUInt32(); // charId.instance (the polling player)
			var targetType = packet.Body.ReadUInt32(); // targetId.type (0x0000C350)
			var targetInstance = packet.Body.ReadUInt32();

			var instance = client.Session.Account.ClientInstance;

			// The reply's third field (cCategory) is the client's overhead NAME-COLOUR category for this target,
			// NOT a live-aggro toggle: 0 = friendly (green), 1 = hostile (red), 2 = neutral (yellow). Proven by live
			// gdb (#256): DispatchUpdateAggroStatus -> FUN_0115eb10 emits the disposition signal that writes the
			// nameplate colour index (color_friendly_npc/color_hostile_npc/color_neutral_npc); retail's cCategory is
			// exactly 0/1/2. We always sent 0, so every NPC rendered green - including hostile mobs. Send 1 for a
			// hostile NPC so its name renders red like retail. (The NpcStats.community we put in the spawn is correct
			// but the client ignores it for the name colour - this RPC is the sole driver.)
			uint category = 0;
			// Resolve the target in the player's OWN instance (copy) of the map (#268), not the default shared zone.
			var player = _simulation.PlayerWorldEntity(client);
			if (player != null && _simulation.World.ZoneForEntity(player).TryGet(targetInstance, out var target) && target.Hostile)
				category = 1;

			await new PacketStream()
				.WriteHeader(ExistingCharacterPostReadyReportServerIdSender, ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, instance), GameServerCarrierOpcodes.IirCarrier)
				.WriteUInt32(targetType)
				.WriteUInt32(targetInstance)
				.WriteUInt32(category) // cCategory: 0 = friendly, 1 = hostile, 2 = neutral
				.SendAsync(client);

			Logger.LogDebug("RequestAggroStatus(target=0x{Target:X8}) -> cCategory={Category}.", targetInstance, category);
		}

		// The single per-session energy loop: drains while sprinting, REGENS when idle, and steps aside while weaving
		// (the weave owns the drain + the 0x200E stream). Persistent - it keeps ticking for the whole session so energy
		// recovers after a weave/sprint - and exits only on disconnect. A PeriodicTimer loop, not a self-rescheduling
		// callback. EnergyLoopActive guards against a second loop.
		private async Task RunEnergyMirror(INetworkClient client)
		{
			client.Session.EnergyLoopActive = true;
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(EnergyTickMs));
				while (await timer.WaitForNextTickAsync())
				{
					var session = client.Session;
					if (session?.Account == null)
						return; // disconnected

					if (session.IsSpellweaving)
						continue; // the weave owns the energy this tick

					if (session.IsSprinting)
						session.Energy -= (uint)Math.Min(SprintEnergyDrainPerTick, (int)session.Energy);
					// Exhausted is client-driven for sprint (it owns the visual); nothing to send here.
					else if (session.Energy < session.EnergyMax)
						session.Energy = Math.Min(session.EnergyMax, session.Energy + EnergyRegenPerTick);
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Energy mirror loop failed.");
			}
			finally
			{
				var session = client.Session;
				if (session != null)
					session.EnergyLoopActive = false;
			}
		}

		// DispatchUpdateSprintbar (0x200E): the ENERGY / sprint bar update - the yellow bar. Body = [u32 energy][u8 01].
		// This is what actually MOVES the yellow bar (the e9f24184 "Spellweaving Cost" gauge is separate accounting).
		// Retail streams it while weaving to drain the energy bar (decode_spellweave, sender GameCharAgentProject/0x54
		// -> GameCharInterfaceProject/instance; energy full = 8910 for the necro).
		private Task SendSprintBar(INetworkClient client, uint energy)
		{
			return new PacketStream()
				.WriteHeader(SprintBarSender, ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceProjectType, client.Session.Account.ClientInstance), GameCharInterfaceProjectDispatchOpcodes.DispatchUpdateSprintbar)
				.WriteUInt32(energy)
				.WriteByte(0x01)
				.SendAsync(client);
		}

		private void TrackSprintEnergy(INetworkClient client, bool sprinting)
		{
			if (client.Session?.Character == null)
				return;

			client.Session.IsSprinting = sprinting;
			EnsureEnergyLoop(client);
		}
	}
}
