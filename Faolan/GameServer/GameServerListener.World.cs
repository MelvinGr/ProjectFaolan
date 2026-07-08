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

		Task IWorldOutbound.BroadcastEntityFade(WorldEntity entity)
		{
			return _broadcaster.BroadcastEntityFade(entity);
		}

		Task IWorldOutbound.BroadcastNpcAggro(WorldEntity npc, WorldEntity target)
		{
			return _broadcaster.BroadcastNpcAggro(npc, target);
		}

		Task IWorldOutbound.BroadcastNpcCombatMode(WorldEntity npc, bool inCombat)
		{
			return _broadcaster.BroadcastNpcCombatMode(npc, inCombat);
		}

		Task IWorldOutbound.BroadcastNpcMove(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcMove(npc);
		}

		Task IWorldOutbound.BroadcastNpcPatrol(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcPatrol(npc);
		}

		// --- IWorldOutbound: the wire side of the simulation tick (broadcaster builds packets; listener owns vitals
		// send + the world-entry replay) -----------------------------------------------------------------------------
		Task IWorldOutbound.BroadcastNpcRespawn(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcRespawn(npc);
		}

		Task IWorldOutbound.BroadcastNpcStop(WorldEntity npc)
		{
			return _broadcaster.BroadcastNpcStop(npc);
		}

		Task IWorldOutbound.PushVitals(INetworkClient client, EntityVitals vitals)
		{
			return SendStatUpdate(client, vitals.ToVitalsBlock(), client.Session.Account.ClientInstance);
		}

		Task IWorldOutbound.ReenterWorld(INetworkClient client)
		{
			return SendWorldEntry(client);
		}
	}
}
