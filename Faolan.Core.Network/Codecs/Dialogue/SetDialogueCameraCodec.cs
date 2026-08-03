using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// DispatchSetDialogueCamera (0x2015) - the cinematic conversation camera bound to the NPC during a conversation
	// (#188). An RPC-message codec (#530 rule 5b), NOT an IIR: the opcode is the 16-bit word in the rpcHeader,
	// GameCharAgent -> GameCharInterface. The routing and the send stay in ConversationService; the body is
	//
	//   [u32 0xC350][u32 npcHandle][u32 0xC350][u32 playerInstance][CameraArgs]
	//
	// CameraArgs is the client's signature order, exactly ten bytes (#410 - this used to be one opaque blob):
	// nShotSize, nShotType, nShotEmphasis, nShotFov, nShotTilt, bAutoface (5 bytes + a bool byte), then
	// i_vHeadPaddingY as a big-endian float. A camera packet that is not ten bytes long is not decodable into these
	// columns, so Parse rejects it rather than importing a row that would rebuild different bytes.
	public static class SetDialogueCameraCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchSetDialogueCamera;

		public static byte[] Build(SetDialogueCamera value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character)
				.WriteUInt32(value.NpcHandle)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(value.PlayerInstance)
				.WriteByte(value.Camera.ShotSize)
				.WriteByte(value.Camera.ShotType)
				.WriteByte(value.Camera.ShotEmphasis)
				.WriteByte(value.Camera.ShotFov)
				.WriteByte(value.Camera.ShotTilt)
				.WriteByte((byte)(value.Camera.Autoface ? 1 : 0))
				.WriteFloat(value.Camera.HeadPaddingY);

			return s.ToArray();
		}

		public static byte[] Build(uint npcHandle, uint playerInstance, CameraArgs camera)
		{
			return Build(new SetDialogueCamera(npcHandle, playerInstance, camera));
		}

		public static SetDialogueCamera Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SetDialogueCameraCodec));
			r.ExpectU32Be(EntityClass.Character, "the NPC entity class");
			var npc = r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "the player entity class");
			var player = r.ReadU32Be();
			var camera = new CameraArgs(r.ReadU8(), r.ReadU8(), r.ReadU8(), r.ReadU8(), r.ReadU8(), r.ReadU8() != 0, r.ReadF32Be());
			r.EnsureAtEnd();

			return new SetDialogueCamera(npc, player, camera);
		}
	}

	public sealed record CameraArgs(byte ShotSize, byte ShotType, byte ShotEmphasis, byte ShotFov, byte ShotTilt, bool Autoface, float HeadPaddingY);

	public sealed record SetDialogueCamera(uint NpcHandle, uint PlayerInstance, CameraArgs Camera);
}
