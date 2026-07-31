using System;
using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Economy;
using Xunit;

namespace Faolan.Tests
{
	// Golden-byte test for GameCharInterface.DispatchRemoveItem (s2c 0x201F) - the live mid-session item REMOVE,
	// the counterpart to DispatchAddItem (0x2020). Decoded from the hoxxling/strickenbla gameplay captures, where
	// every 0x201F removal body is 16 bytes: [container ID32 = 0x71/equipped + charId][u32 pos][u32 amount].
	// We rebuild the body from those fields and assert it matches the capture byte for byte.
	public class DispatchRemoveItemTests
	{
		private const ContainerKind EquippedContainer = ContainerKind.Quest;

		// Captured DispatchRemoveItem body (strickenbla questcomplete frame 3680; identical shape in the hoxxling
		// gameplay captures): container 0x71 (equipped), charId 0x01316D8A, pos 0, amount 0.
		private const string CapturedBody = "0000007101316d8a0000000000000000";
		private const uint CharId = 0x01316d8a;

		[Fact]
		public void RemoveItem_RoundTripsItsFields()
		{
			var value = new RemoveItem(ContainerKind.Bag, CharId, 5, 2);
			var body = RemoveItemCodec.Build(value);

			Assert.Equal(16, body.Length);
			Assert.Equal(value, RemoveItemCodec.Parse(body));
		}

		[Fact]
		public void RemoveItem_MatchesCapture()
		{
			var body = RemoveItemCodec.Build(new RemoveItem(EquippedContainer, CharId, 0, 0));

			Assert.Equal(CapturedBody, Convert.ToHexStringLower(body));
			Assert.Equal(CapturedBody, Convert.ToHexStringLower(RemoveItemCodec.Build(RemoveItemCodec.Parse(Convert.FromHexString(CapturedBody)))));
		}
	}
}
