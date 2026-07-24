using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// The SetDialogueCamera (0x2015) packet that binds the dialogue camera to the NPC during a conversation (#188).
	// Extracted from ConversationService so it builds without a live client: [RpcHeader: GameCharAgent(agentInstance)
	// -> GameCharInterface(liveInstance)][SetDialogueCamera][C350][npcHandle][C350][liveInstance][cameraParams].
	// Byte-exact in ConversationConstructTests; the service keeps the routing/send.
	public static class DialogueCameraBody
	{
		// The camera arguments as they sit on the wire, rebuilt from the row's typed columns (#410 - these were one
		// opaque 10-byte CameraParams blob). Layout is the client's signature order: nShotSize, nShotType,
		// nShotEmphasis, nShotFov, nShotTilt, bAutoface, then i_vHeadPaddingY as a big-endian float.
		public static byte[] BuildParams(ConversationPacket row)
		{
			return new FaolanStream()
				.WriteByte(row.ShotSize ?? 0)
				.WriteByte(row.ShotType ?? 0)
				.WriteByte(row.ShotEmphasis ?? 0)
				.WriteByte(row.ShotFov ?? 0)
				.WriteByte(row.ShotTilt ?? 0)
				.WriteByte((byte)(row.Autoface == true ? 1 : 0))
				.WriteFloat(row.HeadPaddingY ?? 0f)
				.ToArray();
		}

		public static Rpc BuildDialogueCameraPacket(uint agentInstance, uint liveInstance, uint npcHandle, byte[] cameraParams)
		{
			var body = new FaolanStream()
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(npcHandle)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(liveInstance)
				.WriteArray(cameraParams ?? [])
				.ToArray();

			return new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, agentInstance), Endpoints.Endpoint(Endpoints.GameCharInterfaceType, liveInstance),
				(ushort)ConversationScreenDecoder.SetDialogueCamera, body);
		}
	}
}
