using System;
using System.Buffers.Binary;
using Faolan.GameServer;
using Faolan.GameServer.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the constructed create-editor avatar (issue #189): building the SimpleCharFullUpdate (FA015807) from
	// the fixed pedestal pose + the session handle/playfield + the template's decoded looks reproduces the captured
	// editor-avatar record byte-for-byte (the SpawnEditorAvatar golden, lifted from InitialBurst.bin). This is the
	// construction oracle - it proves the avatar is rebuildable through SpawnPacketBuilder (typed transform +
	// byte-identical ProtoCoreCharacterIIR round-trip) rather than replayed verbatim.
	public class CreateEditorAvatarTests
	{
		// The captured record's Iir body (the golden minus the [u32 len][u32 FA015807] carrier prefix).
		private static byte[] CapturedBody()
		{
			return Golden.Bytes("SpawnEditorAvatar")[8..];
		}

		[Fact]
		public void BuildBody_FromCapturedHandleAndPlayfield_ReproducesCapturedRecord()
		{
			var built = CreateEditorAvatarBuilder.BuildBody(
				CreateEditorAvatarBuilder.EditorAvatarHandle,
				CreateEditorAvatarBuilder.EditorPlayfieldInstance);

			Assert.Equal(Convert.ToHexString(CapturedBody()), Convert.ToHexString(built));
		}

		[Fact]
		public void BuildBody_ReaddressesHandle_OnlyHandleFieldChanges()
		{
			var captured = CapturedBody();

			var built = CreateEditorAvatarBuilder.BuildBody(0x000A1234u, CreateEditorAvatarBuilder.EditorPlayfieldInstance);

			Assert.Equal(captured.Length, built.Length);
			// HandleOffset is a body-relative offset (the body IS the FA015807 record's content after [len][type]).
			const int handleOffset = SpawnPacketBuilder.HandleOffset - 8;
			Assert.Equal(0x000A1234u, BinaryPrimitives.ReadUInt32BigEndian(built.AsSpan(handleOffset, 4)));
			for (var i = 0; i < built.Length; i++)
			{
				var inHandleField = i >= handleOffset && i < handleOffset + 4;
				if (!inHandleField)
					Assert.Equal(captured[i], built[i]);
			}
		}

		[Fact]
		public void BuildBody_ThreadsPlayfieldInstance_OnlyVarintChanges()
		{
			var captured = CapturedBody();
			const uint liveToken = 0x004FE1A8; // Strie's session token - another in-range 4-byte varint

			var built = CreateEditorAvatarBuilder.BuildBody(CreateEditorAvatarBuilder.EditorAvatarHandle, liveToken);

			Assert.Equal(captured.Length, built.Length);
			const int varintOffset = 53; // the transform's `18 <varint>` playfield instance
			for (var i = 0; i < built.Length; i++)
			{
				var inVarint = i >= varintOffset && i < varintOffset + 4;
				if (!inVarint)
					Assert.Equal(captured[i], built[i]);
			}
			// the rewritten varint decodes back to the live token
			var decoded = (uint)((built[varintOffset] & 0x7F)
				| ((built[varintOffset + 1] & 0x7F) << 7)
				| ((built[varintOffset + 2] & 0x7F) << 14)
				| ((built[varintOffset + 3] & 0x7F) << 21));
			Assert.Equal(liveToken, decoded);
		}

		[Fact]
		public void BuildBody_RejectsOutOfRangePlayfieldInstance()
		{
			Assert.Throws<ArgumentOutOfRangeException>(() =>
				CreateEditorAvatarBuilder.BuildBody(CreateEditorAvatarBuilder.EditorAvatarHandle, 0x10));
		}
	}
}
