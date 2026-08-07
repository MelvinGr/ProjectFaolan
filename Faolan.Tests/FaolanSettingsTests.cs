using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Faolan.Core.Cli;
using Faolan.Core.Enums;
using Faolan.Tests.Support;
using Microsoft.Extensions.Configuration;
using Xunit;
using Keys = Faolan.Core.Cli.FaolanSettings.Keys;

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
			Assert.Equal("127.0.0.1", config.ServerAddress);
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
			Assert.Equal("http://127.0.0.1:7099/mcp", config.McpUrl); // ...derived from that port, not a second literal
			Assert.True(config.ChatBridgeEnabled);
			Assert.Equal(120_000, config.ChatBridgeBacklogGrace.TotalMilliseconds);
			Assert.Equal(1500, config.ChatBridgePoll.TotalMilliseconds);
			Assert.Equal("cdn-store", config.CdnStore);
			Assert.Equal("127.0.0.1", config.CdnListenAddress);
			Assert.Equal(7081, config.CdnListenPort);

			// The one that is OPTIONAL rather than defaulted, and stays null on purpose - it names a game install.
			// Its readers handle the null; nothing else may. (The two Login: keys are optional too, but their
			// accessors hand back a parsed BigInteger?, so they are checked with the rest of the keypair rather
			// than here.)
			Assert.Null(config.RdbDir);
			Assert.Equal("http://127.0.0.1:7081", config.CdnPublicAddress); // the loopback mirror, at its own port
		}

		// The MCP endpoint's URL is DERIVED from Mcp:Port and is not a setting, so moving the port moves every reader
		// of it (the chat bridge, Faolan.Web's live-session probe) with it. It used to be one - `Mcp:Url`, then
		// `ChatBridge:Url` - holding a second copy of the port: `Mcp:Port 7098` left its readers dialling 7099, which
		// is a bridge that connects to nothing with the port written correctly in the config that broke it. There is
		// no override, because the endpoint binds loopback: an address a port cannot express is one nothing can reach.
		[Theory]
		[InlineData(null, "http://127.0.0.1:7099/mcp")]
		[InlineData("7098", "http://127.0.0.1:7098/mcp")]
		public void TheMcpEndpointsUrl_FollowsTheServersMcpPort(string? port, string expected)
		{
			// A null here means the key is ABSENT, not present-and-null: an in-memory null would override the defaults
			// layer with nothing, which is a state no config file can produce.
			var over = new List<KeyValuePair<string, string?>>();
			if (port != null)
				over.Add(new(Keys.McpPort, port));

			var config = new ConfigurationBuilder()
				.AddInMemoryCollection(FaolanSettings.Defaults)
				.AddInMemoryCollection(over)
				.Build();

			Assert.Equal(expected, config.McpUrl);
		}

		// A malformed value now FAILS instead of quietly becoming the default. `Account:PremiumCharSlots = ten` used
		// to mean 10 - a setting that reads as applied and is not - because the fallback and the parse were the same
		// expression. The message has to name the key, or the operator is hunting a value they cannot see.
		[Theory]
		[InlineData(Keys.AccountPremiumCharSlots, "ten")]
		[InlineData(Keys.McpPort, "not-a-port")]
		[InlineData(Keys.LogPacketBytes, "yes")]
		[InlineData(Keys.ChatBridgePollMs, "soon")]
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

		// A key is spelled ONCE, as a const in FaolanSettings.Keys, and both halves of the class read it from there -
		// the catalog entry and the accessor. So the two cannot name different keys, and the failure that used to be
		// possible (an accessor indexing a key nothing declares, resolving to null and failing at whatever startup
		// first needed the value) is a compile error.
		//
		// This pins the other direction, which the compiler cannot: the consts and the catalog are the SAME set. A
		// const with no entry is a key that has no default and no --help line; an entry whose key is not a const is a
		// literal that got past the extraction.
		[Fact]
		public void TheKeyConstants_AreExactlyTheCatalog()
		{
			var consts = typeof(FaolanSettings.Keys)
				.GetFields(BindingFlags.Public | BindingFlags.Static)
				.Where(f => f.IsLiteral)
				.ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

			Assert.Equal(FaolanSettings.All.Select(s => s.Key).Order(), consts.Values.Order());

			// And the const is NAMED for its key, so `Keys.LogFile` sits beside the `config.LogFile` that reads it and
			// a grep for either finds both. Without this the set can match while the names drift apart.
			foreach (var (name, key) in consts)
				Assert.Equal(name, key.Replace(":", ""));
		}

		// The catalog is also the only place a key is spelled, so a *Command asking for one that is not there is a
		// typo - and it must fail at the declaration rather than resolve to nothing at runtime.
		[Fact]
		public void AnUncataloguedKey_IsARejectedDeclaration()
		{
			Assert.Throws<ArgumentException>(() => FaolanSettings.Options("Database:ConnectionStrings"));
			Assert.NotEmpty(FaolanSettings.Options(Keys.DatabaseConnectionString));
		}

		// --help shows the default it will actually use. It cannot do otherwise now - the text is built from the same
		// record the defaults layer is - and this pins that the description is the SETTING's, not a paraphrase.
		[Fact]
		public void AnOptionsHelpText_CarriesTheCatalogsDefault()
		{
			var option = FaolanSettings.Options(Keys.LogFile)[0];

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
				// An OPTIONAL setting reads back as null: it is written as a commented-out line, so the file names it
				// without setting it.
				foreach (var setting in FaolanSettings.All)
					Assert.Equal(setting.DefaultText, config[setting.Key], StringComparer.OrdinalIgnoreCase);

				// The login keypair is the case that motivates all of this: it has NO default, so a server that
				// configures nothing has no key and checks no passwords. It must not acquire one from the defaults
				// layer - a built-in key is a key everybody has.
				var defaults = new ConfigurationBuilder().AddInMemoryCollection(FaolanSettings.Defaults).Build();
				Assert.Null(defaults[Keys.LoginPrivateKey]);
				Assert.Null(defaults[Keys.LoginPublicKey]);
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
				Keys.AccountPremiumCharSlots => config.AccountPremiumCharSlots,
				Keys.McpPort => config.McpPort,
				Keys.LogPacketBytes => config.LogPacketBytes,
				Keys.ChatBridgePollMs => config.ChatBridgePoll,
				_ => throw new ArgumentException($"No accessor wired for '{key}' in this test.", nameof(key))
			};
		}
	}
}
