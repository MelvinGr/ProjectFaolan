using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
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
				// Other GameCharAgentProject methods are not loading gates and retail's replies aren't decoded yet -
				// log and no-op rather than misparse them as a 0x2000 ping. The dominant one is SetSprintInputState
				// (0x200C, the sprint key - x2092 in captures): a fire-and-forget input-state update that retail does
				// NOT reply to (verified - sprint uploads vastly outnumber ReportDimensionId downloads with no 1:1
				// correlation; the old "answer each sprint with a ReportDimensionId" was a spurious packet, #72). This
				// is the SOLE sprint path: every SetSprintInputState rides this class (receiver 0x5B9756FC), so it is
				// routed here by class id and never reaches the opcode switch (where 0x200C collides with other
				// methods) - so there is no separate sprint case there.
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
	}
}
