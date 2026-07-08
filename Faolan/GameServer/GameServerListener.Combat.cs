using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.AgentServer;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.GameServer;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Protobuf;
using Faolan.Core.Network.Spawn;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.World;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{

		Task IWorldOutbound.ConvertCorpseToLoot(WorldEntity npc)
		{
			return _broadcaster.ConvertCorpseToLoot(npc);
		}

		Task IWorldOutbound.SendDamageNumber(INetworkClient client, uint targetHandle, uint attackerHandle, uint damage)
		{
			return CombatService.SendDamageNumber(client, targetHandle, attackerHandle, damage);
		}

		Task IWorldOutbound.SendDeathBcc(INetworkClient client, uint dyingHandle)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathBcc(dyingHandle)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathEffect(INetworkClient client, uint dyingHandle)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathEffect(dyingHandle)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathPose(INetworkClient client, uint dyingHandle, Vector3 deathPosition)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildDeathPose(dyingHandle, deathPosition)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendDeathTrigger(INetworkClient client, uint dyingHandle, uint killerEndpoint)
		{
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildKilledBy(dyingHandle, killerEndpoint)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendNpcAttackAnimation(INetworkClient client, uint npcHandle, uint damage, uint swingCounter)
		{
			return CombatService.SendNpcAttackAnimation(client, npcHandle, damage, swingCounter);
		}

		Task IWorldOutbound.SendNpcSpellCast(INetworkClient client, uint npcHandle, NpcSpellCast spell)
		{
			return CombatService.SendNpcSpellCast(client, npcHandle, spell);
		}

		Task IWorldOutbound.SendResurrectionScreen(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			return SendValidResurrectionSpots(client, deathPosition, spots);
		}

		Task IWorldOutbound.SendRevive(INetworkClient client, uint handle, uint respawnPointId)
		{
			// Same GameAgent 0x59 sender / receiver framing as the death sequence (retail beach #1382 uses it).
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(WorldDeath.BuildRevive(handle, respawnPointId)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendReviveDestination(INetworkClient client, uint handle, uint mapId, Vector3 position, float facingX, float facingZ)
		{
			// The revive n3Teleport (0x5A32F0D7) that relocates the player to the res point - retail sends it right
			// after the revive IntendedTeleport (#214). Carries the destination position; same-map so no loading screen.
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(TeleportCodec.BuildReviveDestination(handle, mapId, handle, position, facingX, facingZ)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendReviveTeleport(INetworkClient client, uint handle, uint mapId, uint playfieldToken)
		{
			// IntendedTeleport (0x10D27BC0) relocating the revived player to the chosen res point on the SAME map (no
			// loading screen). The caller passes the LIVE playfield token so the client repositions in place (#214).
			return new PacketStream()
				.WriteHeader(PlayerDeathSender, WorldFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s => s.WriteArray(TeleportCodec.BuildReviveTeleport(handle, mapId, handle, playfieldToken)))
				.SendAsync(client);
		}

		Task IWorldOutbound.SendStatDamage(INetworkClient client, uint targetHandle, uint statValue, int delta, uint type, uint casterHandle, uint spellBaseId)
		{
			return CombatService.SendStatDamage(client, targetHandle, statValue, delta, type, casterHandle, spellBaseId);
		}
	}
}
