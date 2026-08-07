using System;
using System.Collections.Generic;
using Faolan.Core.Cli;
using Microsoft.Extensions.Configuration;
using Xunit;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

namespace Faolan.Tests
{
	// `IConfiguration.CdnAddress` is where a CLIENT of the content mirror - the importer's RDB proxy and
	// Faolan.Web's viewer - dials it, so a mirror on another box is one `Cdn:PublicAddress` override rather
	// than one setting per consumer.
	//
	// It is not a second setting: it is `CdnPublicAddress` plus a DEFAULT. The raw key cannot simply carry
	// that default, because the two sides read "unset" differently and both readings are right - to the MIRROR it
	// means "refuse to start", since it would otherwise write a guess into the LocalConfig.xml a patch run pins
	// itself to, and to a client it means "the usual local one", because a client that guesses wrong merely fails
	// to connect.
	//
	// The invariant worth gating is that the result is an absolute URL: its consumers concatenate onto it
	// (`RdbReader` appends the mirror route and then `/res/<md5>`), so a relative string would fail at the HTTP
	// call, far from the config that caused it.
	public class CdnAddressTests
	{
		[Theory]
		// Unset: the local mirror on its default port.
		[InlineData(null, null, "http://127.0.0.1:7081")]
		// Unset, non-default port.
		[InlineData(null, "8080", "http://127.0.0.1:8080")]
		// Configured: taken verbatim. The section's Port does NOT apply - the address carries its own.
		[InlineData("http://192.168.1.5:7081", "9000", "http://192.168.1.5:7081")]
		// A trailing slash is dropped, because a path is appended to this.
		[InlineData("https://mirror.lan/", null, "https://mirror.lan")]
		public void ItIsAlwaysAnAbsoluteUrl(string? publicAddress, string? port, string expected)
		{
			var config = Config((Keys.CdnPublicAddress, publicAddress), (Keys.CdnListenPort, port));

			Assert.Equal(expected, config.CdnPublicAddress);
			Assert.True(Uri.TryCreate(config.CdnPublicAddress, UriKind.Absolute, out _));
		}

		// The BIND address is deliberately not a fallback: `Cdn:ListenAddress` is 0.0.0.0 in a container by
		// design, which is unreachable as a destination - so falling back to it would be wrong in exactly the
		// deployment that has a non-default value to fall back to.
		[Fact]
		public void TheBindAddressIsNotUsedAsADestination()
		{
			var config = Config((Keys.CdnListenAddress, "0.0.0.0"));

			Assert.Equal("http://127.0.0.1:7081", config.CdnPublicAddress);
		}

		// The catalog's defaults UNDER the values named here - the same stack FaolanCommandLine.Build makes, so
		// "unset" here means what it means to a deployment: the default, not a missing key.
		private static IConfiguration Config(params (string Key, string? Value)[] values)
		{
			var pairs = new Dictionary<string, string?>();
			foreach (var (key, value) in values)
				if (value != null)
					pairs[key] = value;

			return new ConfigurationBuilder()
				.AddInMemoryCollection(FaolanSettings.Defaults)
				.AddInMemoryCollection(pairs)
				.Build();
		}
	}
}
