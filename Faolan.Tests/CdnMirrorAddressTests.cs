using System;
using System.Collections.Generic;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Faolan.Tests
{
	// `IConfiguration.CdnMirrorAddress` is the ONE answer to "where is the content mirror" that the importer's RDB
	// proxy and Faolan.Web's viewer both read, so a mirror on another box is a single `CdnMirror` override rather
	// than one setting per consumer.
	//
	// The invariant worth gating is that it is ALWAYS an absolute URL. Its consumers concatenate onto it -
	// `RdbReader` appends the mirror route and then `/res/<md5>` - so a bare `host` would produce a relative string
	// that fails at the HTTP call, far from the config that caused it.
	public class CdnMirrorAddressTests
	{
		[Theory]
		// Nothing configured: the local mirror on its default port.
		[InlineData(null, null, null, "http://127.0.0.1:7081")]
		// The bind address is the fallback, and the section's Port applies to it.
		[InlineData("10.0.0.4", null, "8080", "http://10.0.0.4:8080")]
		// PublicAddress wins over Ip - it is the address a client can actually reach.
		[InlineData("0.0.0.0", "http://192.168.1.5:7081", null, "http://192.168.1.5:7081")]
		// A bare host / host:port is accepted and completed, the same contract Faolan.Web.Content's PublicBase honours.
		[InlineData(null, "192.168.1.5", null, "http://192.168.1.5:7081")]
		[InlineData(null, "192.168.1.5:9000", null, "http://192.168.1.5:9000")]
		// An explicit port on the address beats the section's Port.
		[InlineData(null, "http://mirror.lan:9000", "7081", "http://mirror.lan:9000")]
		// https survives, and a trailing slash is not carried into a concatenated path. The scheme's DEFAULT port is
		// left implicit (`:443` on https is what `https://` already means), which is still an absolute URL.
		[InlineData(null, "https://mirror.lan/", "443", "https://mirror.lan")]
		public void ItIsAlwaysAnAbsoluteUrl(string? ip, string? publicAddress, string? port, string expected)
		{
			var config = Config(("CdnMirror:Ip", ip), ("CdnMirror:PublicAddress", publicAddress), ("CdnMirror:Port", port));

			Assert.Equal(expected, config.CdnMirrorAddress);
			Assert.True(Uri.TryCreate(config.CdnMirrorAddress, UriKind.Absolute, out _));
		}

		private static IConfiguration Config(params (string Key, string? Value)[] values)
		{
			var pairs = new Dictionary<string, string?>();
			foreach (var (key, value) in values)
				if (value != null)
					pairs[key] = value;

			return new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
		}
	}
}
