using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Network;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.CreateFlow
{
	// The character-creation -> editor -> destiny-cutscene -> enter-world flow, extracted out of the
	// GameServerListener god-class (docs/handoff-2026-06-22-create-flow-reconstruct.md). The listener holds this as
	// _createFlow and forwards the inbound create-flow callbacks to it; the flow's outbound packet builders live on
	// the other partials in this folder (CreateWorldHandshake / CreateWorldCutsceneScripts / RetailNpcConversationBurst
	// / the Retail*Burst files). Verbatim/byte-exact burst builders stay `static` so the golden tests reach them
	// unchanged (CreateFlowService.SendWorldPrepBurst / .BuildKalanthesIntroMove / ...).
	//
	// Cross-cutting deps follow the house pattern (CLAUDE.md "composed collaborators"): the world model comes in by
	// ctor, and the one listener-owned outbound op the cutscene needs (SendBurstlessWorldEntry, also used by the
	// existing-char login replay) arrives as a narrow Func seam rather than a back-reference to the listener.
	public sealed partial class CreateFlowService
	{
		private readonly ILogger _logger;
		private readonly IDatabaseRepository _database;

		// The live world simulation (owns the connected players' authoritative entities + zone model). Used by the
		// cutscene actor-binding (LiveNpcHandle) to re-address the destiny cinematic to OUR live Kalanthes.
		private readonly WorldSimulation _simulation;

		// The listener's burst-less world entry (Login/BurstlessWorldEntry.cs) - the constructed live-char world the
		// create-flow enters before the destiny cutscene overlay. Injected as a seam (shared with the login replay).
		private readonly Func<INetworkClient, uint, Task> _sendBurstlessWorldEntry;

		// Character ids that have just been named in the create-editor and are about to open their FIRST world
		// connection (the second GameServer connection, after the 0x2002 enter-world handoff). Added when the name is
		// committed (HandleCommandPacket TheNameIs) and consumed by the listener's world Hello via
		// TryConsumePendingCreateWorldEntry, which is how that connection is recognised as the create-world entry (and
		// given the intro/cutscene) rather than a normal login. Static because the editor and world connections are
		// different sessions but hit the same listener/service instance; ConcurrentDictionary-as-set because it is
		// mutated from different connection threads (add on one connection, consume on the reconnect). Value unused.
		private static readonly ConcurrentDictionary<uint, byte> PendingCreateWorldEntry = new ConcurrentDictionary<uint, byte>();

		public CreateFlowService(ILogger logger, IDatabaseRepository database, WorldSimulation simulation, Func<INetworkClient, uint, Task> sendBurstlessWorldEntry)
		{
			_logger = logger;
			_database = database;
			_simulation = simulation;
			_sendBurstlessWorldEntry = sendBurstlessWorldEntry;
		}

		// Consumes (removes) a pending create-world-entry marker for `clientInstance`, returning true if one was set.
		// The listener's world Hello calls this to recognise the post-creation world connection (and skip the helper
		// auth), then take the create-world entry path; consuming it means a later normal login doesn't re-trigger the intro.
		internal bool TryConsumePendingCreateWorldEntry(uint clientInstance)
		{
			return PendingCreateWorldEntry.TryRemove(clientInstance, out _);
		}

		// The post-creation WORLD connection: the second (world) connection opened after the 0x2002 enter-world
		// handoff, flagged in the Hello handler. Distinguishes it from the editor connection and from a normal
		// existing-character login so the intro/cutscene bursts only fire there.
		private static bool IsCreateWorldConnection(INetworkClient client)
		{
			return client.Session.IsCreateWorldSession && client.Session.Character?.Name != null;
		}
	}
}
