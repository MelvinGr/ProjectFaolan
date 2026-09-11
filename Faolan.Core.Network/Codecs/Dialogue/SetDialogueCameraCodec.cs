using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	/// <summary>
	///     <c>DispatchSetDialogueCamera</c> - the cinematic conversation camera bound to the NPC during a conversation. An
	///     RPC-message codec rather than an IIR: the identity is the method in the rpcHeader, GameCharAgent to
	///     GameCharInterface. The routing and the send stay in <c>ConversationService</c>; the body is
	///     <c>[u32 entityClass][u32 npcHandle][u32 entityClass][u32 playerInstance][CameraArgs]</c>.
	/// </summary>
	/// <remarks>
	///     <c>CameraArgs</c> is the client's signature order, exactly ten bytes: nShotSize, nShotType, nShotEmphasis,
	///     nShotFov, nShotTilt and bAutoface (five bytes plus a bool byte), then <c>i_vHeadPaddingY</c> as a big-endian
	///     float. A camera packet that is not ten bytes long is not decodable into these columns, so <c>Parse</c> rejects
	///     it rather than importing a row that would rebuild different bytes.
	/// </remarks>
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
