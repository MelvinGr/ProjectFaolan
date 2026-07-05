using System;
using System.Threading.Tasks;
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

		// The live world simulation (owns the connected players' authoritative entities + zone model). Used by the
		// cutscene actor-binding (LiveNpcHandle) to re-address the destiny cinematic to OUR live Kalanthes.
		private readonly WorldSimulation _simulation;

		// The listener's burst-less world entry (Login/BurstlessWorldEntry.cs) - the constructed live-char world the
		// create-flow enters before the destiny cutscene overlay. Injected as a seam (shared with the login replay).
		private readonly Func<INetworkClient, uint, Task> _sendBurstlessWorldEntry;

		public CreateFlowService(ILogger logger, WorldSimulation simulation, Func<INetworkClient, uint, Task> sendBurstlessWorldEntry)
		{
			_logger = logger;
			_simulation = simulation;
			_sendBurstlessWorldEntry = sendBurstlessWorldEntry;
		}
	}
}
