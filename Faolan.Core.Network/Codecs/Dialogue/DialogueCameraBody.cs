using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// The SetDialogueCamera (0x2015) packet that binds the dialogue camera to the NPC during a conversation (#188).
	// Extracted from ConversationService so it builds without a live client: [RpcHeader: GameCharAgent(agentInstance)
	// -> GameCharInterface(liveInstance)][SetDialogueCamera][C350][npcHandle][C350][liveInstance][cameraParams].
	// Byte-exact in ConversationConstructTests; the service keeps the routing/send.
	public static class DialogueCameraBody
	{
		public static byte[] BuildDialogueCameraPacket(uint agentInstance, uint liveInstance, uint npcHandle, byte[] cameraParams)
		{
			var body = new FaolanStream()
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(npcHandle)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(liveInstance)
				.WriteArray(cameraParams ?? [])
				.ToArray();

			var ps = new PacketStream().WriteHeader(Endpoints.EndpointBytes(Endpoints.GameCharAgentType, agentInstance), Endpoints.EndpointBytes(Endpoints.GameCharInterfaceType, liveInstance),
				(ushort)ConversationScreenDecoder.SetDialogueCamera);
			ps.WriteArray(body);
			ps.WriteLengthHash();
			return ps.ToArray();
		}
	}
}
