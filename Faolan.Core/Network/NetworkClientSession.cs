using System.Collections.Generic;
using Faolan.Core.Database.Tables;

namespace Faolan.Core.Network
{
	public interface INetworkClientSession
	{
		Account Account { get; set; }

		// Set after CharCreationFinished + the world-prep burst: the creator is now waiting for the client's
		// 0x206E enter-world signal, which we answer with the 0x2002 world-address handoff.
		bool AwaitingEnterWorld { get; set; }

		// Set once the character-creation editor enters its interactive phase (the "IntroDone" script
		// callback). The retail capture proves the server sends NOTHING on the editor connection from
		// IntroDone onward - the client self-advances through customization, naming and enter-world. So
		// once this is set the server must stay silent on the editor connection (no instance-ping acks,
		// no morph acks); any stray downstream packet corrupts the client editor and stalls naming.
		bool CharacterEditorInteractive { get; set; }
		bool HasFlushedCreateWorldEntryBurst { get; set; }
		bool HasPendingCreateEditorReadyFollowUp { get; set; }
		bool HasPendingPost205FFollowUp { get; set; }

		// True on the character-creator (editor) GameServer connection: set in the Hello handler when a
		// not-yet-named character connects.
		bool IsCreateEditorConnection { get; set; }

		// True on the post-creation WORLD GameServer connection (the second connection, opened after the
		// 0x2002 enter-world handoff). Gates the intro/cutscene bursts so they only fire on the first world
		// entry, not on normal existing-character logins.
		bool IsCreateWorldSession { get; set; }
		bool PostMorphBurstSent { get; set; }

		// True once the verbatim world-burst replay has been sent for this connection (existing-character login on
		// the Tortage beach). Gates the 0x2020 -> 0x2008 SpawnCheck responder so it only fires for replay sessions,
		// not the create-world flow (which has its own 0x2020 handling).
		bool UsedWorldBurstReplay { get; set; }

		// The client's conversation id for this connection (the value bytes of the incoming packet's sender
		// field), captured per packet. Auth-handoff responses (UniverseAgent/PlayerAgent) echo this back as the
		// receiver, exactly as retail does, instead of a hardcoded capture-derived id.
		byte[] ClientConversation { get; set; }
		Character Character { get; set; }

		// Live NPC entity handle -> name, captured from the world-burst spawns (FA015807) actually sent this session.
		// NPC dynel handles are session-volatile, so this is how a clicked handle is resolved to the stable NPC name
		// that conversation routing keys on. Populated when the burst is replayed.
		Dictionary<uint, string> SpawnedNpcNamesByHandle { get; }

		// Live NPC entity handle -> max health (player-facing = the spawn's CharacterStats.health / 100), captured
		// from the world-burst spawns. Combat seeds an NPC's vitals from this so its health bar matches what the
		// client rendered from the spawn, instead of a flat placeholder. Populated alongside SpawnedNpcNamesByHandle.
		Dictionary<uint, uint> SpawnedNpcMaxHealthByHandle { get; }

		// Index of the next post-creation world cutscene chunk to send. Advanced once per client script
		// trigger (ClientLoadingDone, StartScript, Move, IntroDone) so each chunk is sent in order.
		int CreateWorldChunkIndex { get; set; }
		List<uint> CreateEditorObservedRequestIds { get; }
		List<uint> CreateEditorReadyRequestIds { get; }

		// Name chosen in the character-creation editor (from the "TheNameIs" script callback).
		// Stored separately from Character.Name so the create-editor flow (which gates on
		// Character.Name == null) is not disturbed mid-stream. The eventual enter-world/commit step
		// will promote this to the persisted character.
		string PendingCharacterName { get; set; }
		uint CreateEditorTempInstanceCount { get; set; }

		// Incrementing sequence number stamped into each 0xBADF5A4B morph-ack the creator sends back while
		// the editor streams its SetMorphValue callbacks. Retail starts this at 2.
		uint MorphAckCounter { get; set; }

		// Debounce for the post-morph trio: after Finish the editor streams its SetMorphValue callbacks (only
		// for morphs that were changed, so the exact set/last one varies). MorphActivitySequence is bumped on
		// Finish and every morph; the trio is sent once activity goes quiet. PostMorphBurstSent guards it.
		uint MorphActivitySequence { get; set; }
		uint Post205F02165EFFCount { get; set; }
		uint Post205F2020Count { get; set; }
		uint Post205FF98E10B3Count { get; set; }
		uint? LastGameServer2020RequestId { get; set; }
		ulong GameServerInboundPacketSequence { get; set; }

		// The active NPC conversation, served one STAGE at a time. A captured conversation node bundles every stage
		// the player walked through (greeting + each option's reply); the runtime segments it into stages (each ends
		// at a Knubot answer-list / close-window packet) so a click serves the greeting and each KnubotAnswer serves
		// the next reply - instead of dumping the whole tree at once. Re-addressed packets, ready to send.
		List<List<byte[]>> ConversationStages { get; set; }
		int ConversationStageIndex { get; set; }

		// Monotonic counter stamped as field3 (the "phase") into each NormalAttack BehaviorControlCenter ack. In the
		// capture it increments by 1 per player swing-ack; the client uses it to sequence the behaviour responses.
		int NormalAttackAckPhase { get; set; }
	}

	public sealed class NetworkClientSession : INetworkClientSession
	{
		private Character _character;

		public Account Account { get; set; }
		public bool AwaitingEnterWorld { get; set; }
		public bool CharacterEditorInteractive { get; set; }
		public bool HasFlushedCreateWorldEntryBurst { get; set; }
		public bool HasPendingCreateEditorReadyFollowUp { get; set; }
		public bool HasPendingPost205FFollowUp { get; set; }
		public bool IsCreateEditorConnection { get; set; }
		public bool IsCreateWorldSession { get; set; }
		public bool PostMorphBurstSent { get; set; }
		public bool UsedWorldBurstReplay { get; set; }
		public byte[] ClientConversation { get; set; }

		public Character Character
		{
			get => _character ?? Account?.Character;
			set => _character = value;
		}

		public Dictionary<uint, string> SpawnedNpcNamesByHandle { get; } = new Dictionary<uint, string>();
		public Dictionary<uint, uint> SpawnedNpcMaxHealthByHandle { get; } = new Dictionary<uint, uint>();

		public int CreateWorldChunkIndex { get; set; }
		public List<uint> CreateEditorObservedRequestIds { get; } = [];
		public List<uint> CreateEditorReadyRequestIds { get; } = [];
		public string PendingCharacterName { get; set; }
		public uint CreateEditorTempInstanceCount { get; set; }
		public uint MorphAckCounter { get; set; }
		public uint MorphActivitySequence { get; set; }
		public uint Post205F02165EFFCount { get; set; }
		public uint Post205F2020Count { get; set; }
		public uint Post205FF98E10B3Count { get; set; }

		public uint? LastGameServer2020RequestId { get; set; }
		public ulong GameServerInboundPacketSequence { get; set; }
		public List<List<byte[]>> ConversationStages { get; set; }
		public int ConversationStageIndex { get; set; }
		public int NormalAttackAckPhase { get; set; }
	}
}
