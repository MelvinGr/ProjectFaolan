using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.CreateFlow
{
	// The post-customization handshake on the character-creator connection, reconstructed byte-for-byte
	// from Captures/Dikketest_rogue-barbarian_create_random-actions.json (creator = gs013:7016).
	//
	// After Finish + the SetMorphValue stream, the server drives the client to naming and enter-world:
	//   1. each non-empty SetMorphValue is acked with a 0xBADF5A4B packet that ECHOES the morph value
	//      string and carries a float + an incrementing counter (this method),
	//   2. after the final morph ("thigh") the server sends a post-morph trio (0xF98E10B3 + a 0xA36D3B74
	//      callback + 0x96B8DC59) that prompts the client to send TheNameIs,
	//   3. TheNameIs is answered with NicknameOk + CharCreationFinished (handled elsewhere),
	//   4. the client's 0x206E enter-world request is answered with a 0x2002 carrying the world address.
	public partial class CreateFlowService
	{
		// Sends the post-morph trio once the morph stream goes quiet. After Finish the editor streams its
		// SetMorphValue callbacks, but ONLY for morphs that were changed - so the set (and which one is last)
		// varies, and if the body is left default the "thigh"/body morphs never arrive. Instead of keying off
		// a specific morph, we debounce: Finish and every morph bump MorphActivitySequence; ~350ms after the
		// last activity (so any morphs have arrived and been acked) we send the trio exactly once.
		internal void SchedulePostMorphBurst(INetworkClient client)
		{
			if (client.Session.PostMorphBurstSent)
				return;

			var sequence = ++client.Session.MorphActivitySequence;

			// FALLBACK ONLY. The deterministic, retail-faithful trigger is FlushPostMorphBurstOnBatchDrained,
			// fired when the TCP recv that carried Finish + the SetMorphValue burst drains (#197 - retail sends
			// the trio once the client's customization batch is fully received, not after a wall-clock delay).
			// This timer only catches the rare case where that burst is split across recvs, so the editor can
			// never hang waiting for the trio; it's a long safety net, not the primary path.
			_ = Task.Run((Func<Task>)(async () =>
			{
				try
				{
					await Task.Delay(1500);

					// A newer morph/Finish arrived (the burst is still streaming) -> a later task / the batch-drain
					// will send it. TrySend also no-ops if it was already sent.
					if (client.Session.MorphActivitySequence != sequence)
						return;

					await TrySendPostMorphBurstOnce(client);
				}
				catch (Exception e)
				{
					_logger.LogWarning(e, "Character-editor: failed to send the post-morph burst (fallback timer).");
				}
			}));
		}

		// #197 deterministic trigger: the client sends Finish + its SetMorphValue batch in one burst; retail sends
		// the post-morph trio once that recv batch is fully received. Called from the listener's batch-drained hook
		// (ReceivedBatchComplete). No-op until Finish/a morph has been seen this session, and sends exactly once.
		internal Task FlushPostMorphBurstOnBatchDrained(INetworkClient client)
		{
			return TrySendPostMorphBurstOnce(client);
		}

		private async Task TrySendPostMorphBurstOnce(INetworkClient client)
		{
			// MorphActivitySequence > 0 means Finish (or a morph) has been processed - i.e. the customization is
			// being committed. Guarded single-send so the batch-drain trigger and the fallback timer can't double it.
			if (client.Session.MorphActivitySequence == 0 || client.Session.PostMorphBurstSent)
				return;

			client.Session.PostMorphBurstSent = true;
			await SendPostMorphBurst(client);
		}

		// Builds an outgoing 0xA36D3B74 script callback: subOpcode + ack(0xC350) + clientInstance +
		// reserved(0) + pbLength + protobuf{field1=5, field2=2, field3=0, field4=token, field5=value,
		// field6={0,0}} - the same wire shape the client uses for its own callbacks.
		private static Task SendA36DCallback(INetworkClient client, string token, string value)
		{
			var instance = client.Session.Account.ClientInstance;
			return new PacketStream()
				.WriteHeader(CreateEditorFraming.MorphAckSender, CreateEditorFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000Opcodes.ScryScriptIIR);
					s.WriteUInt32(EntityClass.Character);
					s.WriteUInt32(instance);
					s.WriteByte(0x00);
					s.WriteArrayUInt32Length(pb =>
					{
						pb.WriteByte(0x08);
						pb.WriteByte(0x05);
						pb.WriteByte(0x10);
						pb.WriteByte(0x02);
						pb.WriteByte(0x18);
						pb.WriteByte(0x00);
						pb.WriteByte(0x22);
						pb.WriteStringByteLength(token);
						pb.WriteByte(0x2A);
						pb.WriteStringByteLength(value);
						pb.WriteArray(0x32, 0x04, 0x08, 0x00, 0x10, 0x00);
					});
				})
				.SendAsync(client);
		}

		// Builds a creator-connection 0x2000 packet: subOpcode + ack(0xC350) + clientInstance + fixed tail.
		private static Task SendCreateEditor2000(INetworkClient client, uint subOpcode, byte[] tail, uint instance)
		{
			return new PacketStream()
				.WriteHeader(CreateEditorFraming.MorphAckSender, CreateEditorFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(subOpcode);
					s.WriteUInt32(EntityClass.Character);
					s.WriteUInt32(instance);
					s.WriteArray(tail);
				})
				.SendAsync(client);
		}

		// Sends a fully-built create-editor IIR record (one that already begins with its sub-opcode, e.g.
		// ScryScript.BuildStartScriptGroup) on the same conversation framing as SendCreateEditor2000.
		private static Task SendCreateEditor2000Record(INetworkClient client, byte[] record)
		{
			return new PacketStream().WriteHeader(CreateEditorFraming.MorphAckSender, CreateEditorFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob).WriteArrayUInt32Length(record).SendAsync(client);
		}

		// Sends the 0xBADF5A4B ack for one SetMorphValue. The wire layout (after the 0x2000 wrapper) is:
		//   subOpcode(0xBADF5A4B) ack(0xC350) clientInstance uint16(0) [byteLen morphValue] float byte(0) counter
		// The float is a server-side default that is not present in the client's message: textures
		// (values starting with '*') use 1.0, body morphs use -0.03 (the only two values seen in retail).
		internal static Task SendMorphValueAck(INetworkClient client, string morphValue)
		{
			var instance = client.Session.Account.ClientInstance;
			var counter = client.Session.MorphAckCounter == 0
				? client.Session.MorphAckCounter = 2
				: ++client.Session.MorphAckCounter;
			// IEEE-754 bits written big-endian (the editor reads these floats big-endian here, unlike
			// FaolanStream.WriteFloat which is little-endian). 1.0f=0x3F800000 for textures (values starting
			// with '*'), -0.03f=0xBCF5C28F for body morphs - the only two values retail uses.
			var floatBits = morphValue.StartsWith("*", StringComparison.Ordinal)
				? 0x3F800000u
				: 0xBCF5C28Fu;

			return new PacketStream()
				.WriteHeader(CreateEditorFraming.MorphAckSender, CreateEditorFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000Opcodes.SetMorphValueIIR);
					s.WriteUInt32(EntityClass.Character);
					s.WriteUInt32(instance);
					s.WriteUInt16(0);
					s.WriteStringByteLength(morphValue);
					s.WriteUInt32(floatBits);
					s.WriteByte(0);
					s.WriteUInt32(counter);
				})
				.SendAsync(client);
		}

		// Step 2: answer the client's TheNameIs callback so it knows the name was accepted and advances to
		// enter-world. Sequence from dikketest (idx 359-363): 0xADCE0CDA (echoes the name) + an 0xA36D3B74
		// "NicknameOk" callback (echoes the name) + 0xF508F4C1 (fixed) + an 0xA36D3B74 "CharCreationFinished".
		internal async Task SendNicknameAccepted(INetworkClient client, string name)
		{
			var instance = client.Session.Account.ClientInstance;

			await new PacketStream()
				.WriteHeader(CreateEditorFraming.MorphAckSender, CreateEditorFraming.Receiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000Opcodes.SetNameIIR);
					s.WriteUInt32(EntityClass.Character);
					s.WriteUInt32(instance);
					s.WriteUInt32(0u);
					s.WriteByte(0x03);
					s.WriteByte(0x01);
					s.WriteUInt32(0u);
					s.WriteStringByteLength(name);
				})
				.SendAsync(client);

			await SendA36DCallback(client, "NicknameOk", name);
			await SendCreateEditor2000(client,
				0xF508F4C1u,
				[
					0x00, 0x00, 0x00, 0x07, 0xE2, 0x00, 0x00, 0xCF, 0xEF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
					0x00, 0x09, 0x00, 0x00, 0x46, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00
				],
				instance);
			await SendA36DCallback(client, "CharCreationFinished", "");

			// Step 3: the world-prep / scene burst that lets the client proceed to enter-world (0x206E).
			await SendWorldPrepBurst(client);

			// Step 3b: grant the class's starting SPELLS so the client places them on the starting bar. The
			// world-prep burst hardcodes only the 0x2052 ABILITY grants; retail also sends the class spells as
			// DispatchLearnSpell (0x200A) on the same GameCharInterface framing, during create right BEFORE the
			// client's 0x206E bar drags (Blablatest necro f239: Ice Strike R1 + Frozen Hatred). Class-aware, so
			// casters get spells and melee get none (matching the barbarian capture the burst was seeded from).
			// With the client's bar drags now persisted, these land on the bar (the client only bars what it was
			// granted); without it a fresh caster's bar has abilities but no spells.
			await GrantCreateStartingSpells(client);

			// The client will now send 0x206E (enter world); answer it with the 0x2002 world handoff.
			client.Session.AwaitingEnterWorld = true;
		}

		// Grants the character's class-starting spells during create as DispatchLearnSpell (0x200A) packets - the
		// spell counterpart to the world-prep burst's hardcoded 0x2052 ability grants. The client bars each granted
		// spell, so these MUST arrive before its 0x206E bar drags. The body is just the 4-byte spell id (retail
		// f239). Melee classes resolve to an empty set (no starting spells).
		private async Task GrantCreateStartingSpells(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var id = client.Session.Account.ClientInstance;
			var sender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharAgentType, 0x0C);
			var receiver = ConanEndpoints.EndpointBytes(ConanEndpoints.GameCharInterfaceType, id);

			var spells = await _database.GetStartingSpells(character.Level, character.Class);
			foreach (var spellId in spells)
				await new PacketStream()
					.WriteHeader(sender, receiver, GameCharInterfaceDispatchOpcodes.DispatchLearnSpell)
					.WriteUInt32(spellId)
					.SendAsync(client);

			_logger.LogInformation("Create: granted {Count} starting spell(s) (0x200A) to '{Name}' (class={Class}) so the client bars them.", spells.Length, character.Name, character.Class);
		}

		// Reject a chosen name so the client re-prompts instead of dead-ending (no loading screen). Retail answers a
		// bad TheNameIs with the same 0xA36D3B74 callback as NicknameOk but a different token: NicknameTaken (already in
		// use, echoes the name) or NicknameInvalid (empty/unsaveable, empty value). Verbatim grammar from
		// Captures/Stricken_soldier-guardian_create_random-actions (frames 151 taken / 158 invalid / 193 ok). The client
		// then simply re-sends TheNameIs with a new name - no other packets are needed.
		internal static Task SendNicknameRejected(INetworkClient client, string name, bool taken)
		{
			return taken
				? SendA36DCallback(client, "NicknameTaken", name)
				: SendA36DCallback(client, "NicknameInvalid", "");
		}

		// The fixed trio the server sends after the last morph; tails are verbatim from dikketest with the
		// client instance substituted. This is what nudges the client to send the TheNameIs callback.
		private static async Task SendPostMorphBurst(INetworkClient client)
		{
			var instance = client.Session.Account.ClientInstance;

			await SendCreateEditor2000(client, 0xF98E10B3u, [0x01, 0x00, 0x00, 0x05, 0x78, 0x00, 0x00, 0x00, 0x00], instance);
			await SendCreateEditor2000Record(client, ScryScript.BuildStartScriptGroup(instance, 35820)); // StartScriptGroupOnClient (#223)
			await SendCreateEditor2000(client,
				0x96B8DC59u,
				[0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x2A, 0xF8, 0x00, 0x00, 0x01, 0xF9, 0x00, 0x00, 0x43, 0x30, 0x00, 0x00, 0x01, 0xFB, 0x00, 0x00, 0x00, 0x00],
				instance);
		}

	}
}
