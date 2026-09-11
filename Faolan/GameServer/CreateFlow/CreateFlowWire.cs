using System.Threading.Tasks;
using Faolan.Core.Constants;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;

namespace Faolan.GameServer.CreateFlow
{
	// The create flow's senders: the A36D script callbacks, the create-editor 0x2000 records, the scry scripts and the
	// two endpoint constants they frame against. Shared by the editor bursts, the name commit, the morph ack and the
	// cutscene, which is why they are one static class rather than private members of any of them.
	internal static class CreateFlowWire
	{
		internal static readonly ProtoEndpoint MorphAckSender = Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x0C);

		// The create-editor's own grant conversation (GameCharAgent 0x0C); the login/world grants ride a different one.
		internal static readonly ProtoEndpoint CreateGrantSender = Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x0C);

		// The create-editor 0x2000 receiver: the player object (GameInterfaceType) addressed to this session's
		// client instance. Built as a typed ProtoEndpoint so the type and instance cannot drift - a hardcoded
		// receiver would bake in one session's instance. See Endpoints and ProtoEndpointGoldenTests.
		internal static ProtoEndpoint Receiver(INetworkClient client)
		{
			return client.Endpoint(RpcInterfaces.GameInterfaceType, client.Session.Account.ClientInstance);
		}

		// The create-flow send idiom: one 0x2000 record ([u32 len][record]) delivered via DispatchPassBlob, where the
		// record is a codec's `Build` output and therefore already carries its own sub-opcode. Every editor/world-prep
		// burst funnels its records through here instead of re-spelling the PacketStream chain.
		internal static Task SendIirRecord(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, byte[] record)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
		}

		// Sends a fully-built create-editor IIR record - one that already begins with its sub-opcode, i.e. whatever
		// `XCodec.Build(...)` returned - on the creator connection.
		//
		// There is deliberately no `(subOpcode, tail)` twin: that shape makes every burst step name an IIR in the
		// service, which is what the codec conventions forbid, and it forces a helper whose whole job is slicing the
		// sub-opcode back off a record so this can re-prepend it.
		internal static Task SendCreateEditor2000Record(INetworkClient client, byte[] record)
		{
			return client.SendAsync(new Rpc(MorphAckSender, Receiver(client), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(record)));
		}

		// Emits a GameActionListIIR (0xF508F4C1) carrier for `dynel`, delivered to frame `id`. The fixed record
		// header (subop/type/Head/four/one, decoded 570/570) is constructed via GameActionListCodec.Build and
		// the still-opaque property-map action tail rides verbatim; the carrier length is computed from the body.
		internal static Task SendGameActionListRecord(INetworkClient client, byte agentSub, uint id, uint dynel, byte[] actionTail)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, agentSub), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(GameActionListCodec.Build(new GameActionListIIR(id, dynel, 0u, actionTail)))));
		}

		// Builds an outgoing 0xA36D3B74 script callback: subOpcode + ack(0xC350) + clientInstance + reserved(0) +
		// pbLength + a ProtoScryScriptIIR - the same wire shape (and the same message) the inbound
		// HandleCommandPacket already reads typed. Written as raw tag bytes.
		internal static Task SendA36DCallback(INetworkClient client, string token, string value)
		{
			return SendCreateEditor2000Record(client,
				ScryScriptCodec.Build(client.Session.Account.ClientInstance, token, (uint)ProtoScryScriptIIR.Type.ScriptMessage, resources: value));
		}

		// A ScryScriptIIR step on the GameAgent->GameInterface conversation (the framing the verbatim burst uses).
		internal static Task SendScry(INetworkClient client, byte[] scry)
		{
			var id = client.Session.Account.ClientInstance;
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameAgentType, 0x08), Endpoints.Endpoint(RpcInterfaces.GameInterfaceType, id), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(scry)));
		}
	}
}
