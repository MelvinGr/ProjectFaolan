using System;
using Faolan.Core.Database.Tables;
using Xunit;

namespace Faolan.Tests
{
	// The ChatServer / CsPlayerAgent handoffs carry the realm address as a fixed u32 (byte-exact against retail's
	// ChatServerConnectReady / CSServerConnectReady), and the retail client is IPv4-only - both of its connect helpers
	// are hard-wired AF_INET + gethostbyname. So `Realm.IpAddressLong` must refuse anything that is not an
	// IPv4 literal rather than truncating a v6 address to its first four bytes, which `GetAddressBytes()` would
	// happily do and which would ship a handoff to a host that does not exist.
	public class RealmHandoffAddressTests
	{
		[Fact]
		public void AnIpv4Literal_IsTheBigEndianU32()
		{
			var realm = new Realm { Name = "r", IpAddress = "10.1.2.3" };
			Assert.Equal(0x0A010203u, realm.IpAddressLong);
		}

		[Theory]
		[InlineData("::1")]
		[InlineData("2001:db8::1")]
		[InlineData("::ffff:10.1.2.3")] // v4-mapped is still a 16-byte address on the client's side
		public void AnIpv6Literal_IsRefused_NotTruncated(string address)
		{
			var realm = new Realm { Name = "r", IpAddress = address };
			var ex = Assert.Throws<InvalidOperationException>(() => realm.IpAddressLong);
			Assert.Contains("IPv4", ex.Message);
		}
	}
}
