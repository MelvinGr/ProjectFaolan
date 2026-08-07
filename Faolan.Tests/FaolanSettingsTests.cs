using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Faolan.Core.Cli;
using Faolan.Core.Enums;
using Faolan.Tests.Support;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Faolan.Tests
{
	// A setting is declared ONCE - in FaolanSettings - and everything else is a view of that: a *Command's `--help`
	// line, the DEFAULTS layer at the bottom of the configuration stack, and the generated faolan.json.
	//
	// The bug this pins is the one that shape removes. A setting used to be spelled out twice, in a `*Command.cs`
	// (name + description) and again in `IConfigurationExtensions` (name + `?? "faolan.log"`), with nothing tying
	// them together: `--help` could describe a default the code did not use, and ClientVersion / ChatBridge:PollMs /
	// ChatBridge:BacklogGraceMs had a fallback and NO declaration at all, so no --help in the tree mentioned them.
	//
	// So the gate is: every accessor must resolve against the catalog ALONE. An accessor reading a key nobody
	// catalogued now throws, and it throws HERE rather than at whatever startup first needs the value.
	public class FaolanSettingsTests
	{
		// The catalog is the whole configuration: with no faolan.json, no user file, no command line and no
		// environment, every accessor still answers. That is what lets a container run on env vars alone, and it is
		// what makes a read site's own fallback dead code.
		[Fact]
		public void EveryAccessor_ResolvesAgainstTheDefaultsAlone()
		{
			var config = new ConfigurationBuilder().AddInMemoryCollection(FaolanSettings.Defaults).Build();

			Assert.Equal(FaolanDbProvider.Sqlite, config.DatabaseProvider);
			Assert.Equal("Data Source=faolan.db", config.DatabaseConnectionString);
			Assert.Equal("127.0.0.1", config.ServerIpAddress);
			Assert.Equal(7010, config.UniverseAgentPort);
			Assert.Equal(7011, config.PlayerAgentPort);
			Assert.Equal("Welcome to Project Faolan!", config.WelcomeString);
			Assert.Equal("v5.01.0@602248", config.ClientVersion);
			Assert.True(config.LoginAllowUnpatchedClients);
			Assert.True(config.AccountPremium);
			Assert.Equal(10u, config.AccountPremiumCharSlots);
			Assert.Equal(4u, config.AccountFreeCharSlots);
			Assert.Equal("faolan.log", config.LogFile);
			Assert.False(config.LogPacketBytes);
			Assert.False(config.McpEnabled);
			Assert.Equal(7099, config.McpPort);
			Assert.Equal("http://127.0.0.1:7099/mcp", config.ChatBridgeUrl); // ...derived from that port, not a second literal
			Assert.Equal(120_000, config.ChatBridgeBacklogGraceMs);
			Assert.Equal(1500, config.ChatBridgePollMs);
			Assert.Equal("mirror-store", config.CdnStore);
			Assert.Equal("127.0.0.1", config.CdnListenAddress);
			Assert.Equal(7081, config.CdnListenPort);

			// The two that are OPTIONAL rather than defaulted, and stay null on purpose - one names a game install,
			// the other a deployment's public address (#552). Their readers handle the null; nothing else may.
			// (Login:PublicKey is the third, and DhKeys rather than an accessor here reads it.)
			Assert.Null(config.RdbDir);
			Assert.Null(config.CdnPublicAddress);
			Assert.Equal("http://127.0.0.1:7081", config.CdnPublicAddress); // ...derived from the mirror's own port
		}

		// The chat bridge dials the endpoint the SERVER hosts, so moving the server's port has to move the bridge with
		// it. `Mcp:Url` used to be a second key holding a second copy of the port: `Mcp:Port 7098` left the bridge on
		// 7099 - a bridge that connects to nothing, with the port written correctly in the config that broke it. An
		// explicit `ChatBridge:Url` still wins, for the case a port cannot express (a server on another host).
		[Theory]
		[InlineData(null, null, "http://127.0.0.1:7099/mcp")]
		[InlineData("7098", null, "http://127.0.0.1:7098/mcp")]
		[InlineData("7098", "http://gameserver.lan:7099/mcp", "http://gameserver.lan:7099/mcp")]
		public void TheChatBridgesTarget_FollowsTheServersMcpPort(string? port, string? url, string expected)
		{
			// A null here means the key is ABSENT, not present-and-null: an in-memory null would override the defaults
			// layer with nothing, which is a state no config file can produce.
			var over = new List<KeyValuePair<string, string?>>();
			if (port != null)
				over.Add(new("Mcp:Port", port));
			if (url != null)
				over.Add(new("ChatBridge:Url", url));

			var config = new ConfigurationBuilder()
				.AddInMemoryCollection(FaolanSettings.Defaults)
				.AddInMemoryCollection(over)
				.Build();

			Assert.Equal(expected, config.ChatBridgeUrl);
		}

		// A malformed value now FAILS instead of quietly becoming the default. `Account:PremiumCharSlots = ten` used
		// to mean 10 - a setting that reads as applied and is not - because the fallback and the parse were the same
		// expression. The message has to name the key, or the operator is hunting a value they cannot see.
		[Theory]
		[InlineData("Account:PremiumCharSlots", "ten")]
		[InlineData("Mcp:Port", "not-a-port")]
		[InlineData("Log:PacketBytes", "yes")]
		[InlineData("ChatBridge:PollMs", "soon")]
		public void AMalformedValue_ThrowsNamingTheKey(string key, string value)
		{
			var config = new ConfigurationBuilder()
				.AddInMemoryCollection(FaolanSettings.Defaults)
				.AddInMemoryCollection([new(key, value)])
				.Build();

			var error = Assert.Throws<InvalidOperationException>(() => Read(config, key));
			Assert.Contains(key, error.Message);
			Assert.Contains(value, error.Message);
		}

		// The catalog is also the only place a key is spelled, so a *Command asking for one that is not there is a
		// typo - and it must fail at the declaration rather than resolve to nothing at runtime.
		[Fact]
		public void AnUncataloguedKey_IsARejectedDeclaration()
		{
			Assert.Throws<ArgumentException>(() => FaolanSettings.Options("Database:ConnectionStrings"));
			Assert.NotEmpty(FaolanSettings.Options("Database:ConnectionString"));
		}

		// --help shows the default it will actually use. It cannot do otherwise now - the text is built from the same
		// record the defaults layer is - and this pins that the description is the SETTING's, not a paraphrase.
		[Fact]
		public void AnOptionsHelpText_CarriesTheCatalogsDefault()
		{
			var option = FaolanSettings.Options("Log:File")[0];

			Assert.Equal("--Log:File", option.Name);
			Assert.Contains("(default: faolan.log)", option.Description);
		}

		// The generated file round-trips: written out, read back through the configuration stack, every setting is
		// the value the catalog holds. A file that merely LOOKS right is the failure mode here - a stray comma or a
		// quoted port would load as something else, or not at all.
		[Fact]
		public void TheGeneratedDefaultJson_ReadsBackAsTheCatalog()
		{
			var path = Path.Combine(Path.GetTempPath(), $"faolan-defaults-{Guid.NewGuid():N}.json");
			try
			{
				FaolanSettings.WriteDefaultJson(path);
				var config = new ConfigurationBuilder().AddJsonFile(path).Build();

				// Case-insensitive because a JSON `true` comes back through the reader as "True", which is the same
				// value to every parser that reads it - the catalog spells it lower-case because JSON does.
				//
				// A Hidden setting reads back as null on purpose: its default applies (it is in the defaults layer)
				// but is not WRITTEN here, because it is key material and a config file full of key literals invites
				// someone to edit half of a pair. The optional ones are null for the plainer reason.
				foreach (var setting in FaolanSettings.All)
					Assert.Equal(setting.Hidden ? null : setting.Default, config[setting.Key], StringComparer.OrdinalIgnoreCase);

				// ...and Hidden really does mean "applies anyway", or it would just be a missing default.
				Assert.Equal(FaolanSettings.Get("Login:PrivateKey").Default, new ConfigurationBuilder().AddInMemoryCollection(FaolanSettings.Defaults).Build()["Login:PrivateKey"]);
			}
			finally
			{
				File.Delete(path);
			}
		}

		// The committed faolan.json IS that generated file (this checkout's own deviations live in the gitignored
		// faolan.user.json), so every key in it must be a catalog key. This is what catches a setting RENAMED in code and left behind in the
		// config: the stale key loads fine, is read by nobody, and the deployment silently runs on the default.
		[Fact]
		public void TheCommittedConfig_NamesOnlyCatalogKeys()
		{
			var config = new ConfigurationBuilder().AddJsonFile(TestPaths.Repo("faolan.json")).Build();

			var keys = config.AsEnumerable()
				.Where(pair => pair.Value != null) // a section itself has no value, only its leaves do
				.Select(pair => pair.Key);

			foreach (var key in keys)
				FaolanSettings.Get(key); // throws, naming the key, if the catalog does not have it
		}

		// Reads one key through the accessor that owns it - the parse under test.
		private static object Read(IConfiguration config, string key)
		{
			return key switch
			{
				"Account:PremiumCharSlots" => config.AccountPremiumCharSlots,
				"Mcp:Port" => config.McpPort,
				"Log:PacketBytes" => config.LogPacketBytes,
				"ChatBridge:PollMs" => config.ChatBridgePollMs,
				_ => throw new ArgumentException($"No accessor wired for '{key}' in this test.", nameof(key))
			};
		}
	}
}
