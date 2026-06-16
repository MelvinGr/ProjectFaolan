using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	// The post-customization handshake on the character-creator connection, reconstructed byte-for-byte
	// from Captures/account-stricken998-character-dikketest-create-random-actions.json (creator = gs013:7016).
	//
	// After Finish + the SetMorphValue stream, the server drives the client to naming and enter-world:
	//   1. each non-empty SetMorphValue is acked with a 0xBADF5A4B packet that ECHOES the morph value
	//      string and carries a float + an incrementing counter (this method),
	//   2. after the final morph ("thigh") the server sends a post-morph trio (0xF98E10B3 + a 0xA36D3B74
	//      callback + 0x96B8DC59) that prompts the client to send TheNameIs,
	//   3. TheNameIs is answered with NicknameOk + CharCreationFinished (handled elsewhere),
	//   4. the client's 0x206E enter-world request is answered with a 0x2002 carrying the world address.
	public partial class GameServerListener
	{
		// Server->client conversation family for the creator 0x2000 traffic. Sender is the game-server
		// conversation id; receiver is built per session by CreateEditorReceiver(client) (the per-client
		// family ending in the character-instance varint).
		private static readonly byte[] CreateEditorMorphAckSender = ConanEndpoints.EndpointBytes(ConanEndpoints.GameAgentType, 0x0C);

		// Sends the post-morph trio once the morph stream goes quiet. After Finish the editor streams its
		// SetMorphValue callbacks, but ONLY for morphs that were changed - so the set (and which one is last)
		// varies, and if the body is left default the "thigh"/body morphs never arrive. Instead of keying off
		// a specific morph, we debounce: Finish and every morph bump MorphActivitySequence; ~350ms after the
		// last activity (so any morphs have arrived and been acked) we send the trio exactly once.
		private void SchedulePostMorphBurst(INetworkClient client)
		{
			if (client.Session.PostMorphBurstSent)
				return;

			var sequence = ++client.Session.MorphActivitySequence;

			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(350);

					// A newer morph/Finish arrived, or another scheduled task already sent it -> let the latest win.
					if (client.Session.MorphActivitySequence != sequence || client.Session.PostMorphBurstSent)
						return;

					client.Session.PostMorphBurstSent = true;
					await SendPostMorphBurst(client);
				}
				catch (Exception e)
				{
					Logger.LogWarning(e, "Character-editor: failed to send the post-morph burst.");
				}
			});
		}

		// Builds an outgoing 0xA36D3B74 script callback: subOpcode + ack(0xC350) + clientInstance +
		// reserved(0) + pbLength + protobuf{field1=5, field2=2, field3=0, field4=token, field5=value,
		// field6={0,0}} - the same wire shape the client uses for its own callbacks.
		private Task SendA36DCallback(INetworkClient client, string token, string value)
		{
			var instance = client.Session.Account.ClientInstance;
			return new PacketStream()
				.WriteHeader(CreateEditorMorphAckSender, CreateEditorReceiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000RespondsOpcodes.ScryScriptIIR);
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
		private Task SendCreateEditor2000(INetworkClient client, uint subOpcode, byte[] tail, uint instance)
		{
			return new PacketStream()
				.WriteHeader(CreateEditorMorphAckSender, CreateEditorReceiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(subOpcode);
					s.WriteUInt32(EntityClass.Character);
					s.WriteUInt32(instance);
					s.WriteArray(tail);
				})
				.SendAsync(client);
		}

		// Sends the 0xBADF5A4B ack for one SetMorphValue. The wire layout (after the 0x2000 wrapper) is:
		//   subOpcode(0xBADF5A4B) ack(0xC350) clientInstance uint16(0) [byteLen morphValue] float byte(0) counter
		// The float is a server-side default that is not present in the client's message: textures
		// (values starting with '*') use 1.0, body morphs use -0.03 (the only two values seen in retail).
		private Task SendMorphValueAck(INetworkClient client, string morphValue)
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
				.WriteHeader(CreateEditorMorphAckSender, CreateEditorReceiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000RespondsOpcodes.SetMorphValueIIR);
					s.WriteUInt32(EntityClass.Character);
					s.WriteUInt32(instance);
					s.WriteUInt16((ushort)0);
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
		private async Task SendNicknameAccepted(INetworkClient client, string name)
		{
			var instance = client.Session.Account.ClientInstance;

			await new PacketStream()
				.WriteHeader(CreateEditorMorphAckSender, CreateEditorReceiver(client), GameInterfaceDispatchOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(s =>
				{
					s.WriteUInt32(GameServer0X2000RespondsOpcodes.SetNameIIR);
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
			await SendCreateEditor2000(client, 0xF508F4C1u, [
				0x00, 0x00, 0x00, 0x07, 0xE2, 0x00, 0x00, 0xCF, 0xEF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
				0x00, 0x09, 0x00, 0x00, 0x46, 0x37, 0x00, 0x00, 0x00, 0x00, 0x00
			], instance);
			await SendA36DCallback(client, "CharCreationFinished", "");

			// Step 3: the world-prep / scene burst that lets the client proceed to enter-world (0x206E).
			await SendWorldPrepBurst(client);

			// The client will now send 0x206E (enter world); answer it with the 0x2002 world handoff.
			client.Session.AwaitingEnterWorld = true;
		}

		// The fixed trio the server sends after the last morph; tails are verbatim from dikketest with the
		// client instance substituted. This is what nudges the client to send the TheNameIs callback.
		private async Task SendPostMorphBurst(INetworkClient client)
		{
			var instance = client.Session.Account.ClientInstance;

			await SendCreateEditor2000(client, 0xF98E10B3u, [0x01, 0x00, 0x00, 0x05, 0x78, 0x00, 0x00, 0x00, 0x00], instance);
			await SendCreateEditor2000(client, 0xA36D3B74u, [0x00, 0x00, 0x00, 0x00, 0x0E, 0x08, 0x05, 0x10, 0x00, 0x18, 0xEC, 0x97, 0x02, 0x32, 0x04, 0x08, 0x00, 0x10, 0x00], instance);
			await SendCreateEditor2000(client, 0x96B8DC59u,
				[0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x2A, 0xF8, 0x00, 0x00, 0x01, 0xF9, 0x00, 0x00, 0x43, 0x30, 0x00, 0x00, 0x01, 0xFB, 0x00, 0x00, 0x00, 0x00], instance);
		}

		// Step 4: the enter-world handoff. The client learns the world server address ONLY from this 0x2002
		// packet (the address is carried explicitly here, not derived). The client opens a brand-new connection
		// to that address, which triggers the loading screen where the intro cinematic resources load. Retail used
		// a SEPARATE world port (creator + 1); we advertise the SAME GameServer port, so the fresh connection
		// lands back on the one GameServerListener (which serves the create-world entry path for the now-named
		// character). Body = [0x00][byteLen "host:port"].
		private async Task SendWorldHandoff(INetworkClient client)
		{
			var realm = await Database.GetRealm(client.Session.Character.RealmId);
			var address = $"{realm.IpAddress}:{realm.Port}";

			await new PacketStream()
				.WriteHeader(CreateEditorMorphAckSender, CreateEditorReceiver(client), GameInterfaceDispatchOpcodes.DispatchRedirectToServer)
				.WriteByte(0x00)
				.WriteStringByteLength(address)
				.SendAsync(client);

			Logger.LogInformation("Create-editor: sent enter-world handoff (0x2002) advertising world server {Address}.", address);
		}
	}
}
