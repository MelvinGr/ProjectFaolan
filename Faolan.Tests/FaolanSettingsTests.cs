using System;
using System.Collections.Generic;
using System.CommandLine;
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
			Assert.False(config.ChatBridgeEnabled); // ...its own default is off, and it ALSO requires Mcp:Enabled
			Assert.Equal(120_000, config.ChatBridgeBacklogGrace.TotalMilliseconds);
			Assert.Equal(1500, config.ChatBridgePoll.TotalMilliseconds);
			Assert.Equal("./cdn-store", config.CdnStore);
			Assert.Equal("0.0.0.0", config.CdnListenAddress); // the interface the mirror BINDS, not an address to dial
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
		//
		// This is the FILE/ENVIRONMENT half of the typed catalog: the command line is checked by the parser, which
		// cannot see a faolan.json, so the accessor parses the same declared type and words the same complaint.
		[Theory]
		[InlineData(Keys.AccountPremiumCharSlots, "ten")]
		[InlineData(Keys.McpPort, "not-a-port")]
		[InlineData(Keys.LogPacketBytes, "yes")]
		[InlineData(Keys.ChatBridgePollMs, "soon")]
		[InlineData(Keys.DatabaseProvider, "mysql")]
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

			Assert.Equal(FaolanSettings.All.Select(FaolanSettings.KeyOf).Order(), consts.Values.Order());

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

		// --help shows the default it will actually use. It cannot do otherwise now - the option the parser prints IS
		// the one the defaults layer is built from - and this pins that the description is the SETTING's, not a
		// paraphrase, and that the default is rendered by System.CommandLine rather than pasted into the description.
		[Fact]
		public void AnOptionsHelpText_CarriesTheCatalogsDefault()
		{
			var option = FaolanSettings.Get(Keys.LogFile);

			Assert.Equal("--Log:File", option.Name);
			Assert.Equal("Log file path.", option.Description);
			Assert.Equal("faolan.log", option.GetDefaultValue());
			Assert.Contains("--Log:File <Log:File>  Log file path. [default: faolan.log]", Help(option));
		}

		// The type is the point of the catalog entry being an Option<T>. A value the setting cannot hold is refused by
		// the PARSER, naming the option - not accepted here and then thrown at whatever startup first reads it, which
		// is where `--Mcp:Port banana` used to land (every setting reached the parser as an Option<string>).
		[Theory]
		[InlineData(Keys.McpPort, "banana", "UInt16")]
		[InlineData(Keys.AccountPremiumCharSlots, "ten", "UInt32")]
		[InlineData(Keys.ServerUniverseAgentPort, "70000", "UInt16")] // in range for the int it used to be declared as
		[InlineData(Keys.DatabaseProvider, "mysql", "FaolanDbProvider")]
		public void AMalformedValueOnTheCommandLine_IsAParseError(string key, string value, string type)
		{
			var option = FaolanSettings.Get(key);
			var result = new RootCommand("test") { option }.Parse([option.Name, value]);

			var error = Assert.Single(result.Errors);
			Assert.Contains(option.Name, error.Message);
			Assert.Contains(type, error.Message);
			Assert.Contains(value, error.Message);
		}

		// ...and the value the parser produces reaches the configuration stack spelled the way the catalog spells its
		// defaults: the enum by its own NAME whatever case was typed (so the accessor never sees `postgres`), a bool
		// lower-cased, a port as digits. One renderer for both layers is what makes that true.
		[Theory]
		[InlineData(Keys.McpPort, "7098", "7098")]
		[InlineData(Keys.DatabaseProvider, "postgres", "Postgres")]
		[InlineData(Keys.LogPacketBytes, "True", "true")]
		[InlineData(Keys.LogFile, "server.log", "server.log")]
		public void AGivenValue_OverridesTheConfigurationAsTheCatalogSpellsIt(string key, string given, string expected)
		{
			var option = FaolanSettings.Get(key);
			var result = new RootCommand("test") { option }.Parse([option.Name, given]);

			Assert.Empty(result.Errors);
			Assert.Equal(new Dictionary<string, string?> { [key] = expected }, FaolanCommandLine.Overrides(result, option));
		}

		// A bool setting is a FLAG: `--Mcp:Enabled` on its own means true (as Option<string> it demanded a value), and
		// an explicit false is still allowed. What it must NOT do is contribute an override when it was not passed -
		// an absent bool has an implicit result carrying its default, so a `GetResult(...) != null` reading of "was it
		// given?" would shadow faolan.json with the default on every single run.
		[Fact]
		public void AFlagSetting_OverridesOnlyWhenItIsGiven()
		{
			var option = FaolanSettings.Get(Keys.McpEnabled);
			var command = new RootCommand("test") { option };

			Assert.Empty(FaolanCommandLine.Overrides(command.Parse([]), option));
			Assert.Equal("true", FaolanCommandLine.Overrides(command.Parse([option.Name]), option)[Keys.McpEnabled]);
			Assert.Equal("false", FaolanCommandLine.Overrides(command.Parse([option.Name, "false"]), option)[Keys.McpEnabled]);

			// A word that is neither is still refused - but as an UNMATCHED TOKEN rather than a type error, because a
			// flag's value is optional and the parser simply does not take it. The complaint names the word, not the
			// option, which is why it is pinned here instead of beside the typed values above.
			var confused = command.Parse([option.Name, "yes"]);
			Assert.NotEmpty(confused.Errors);
			Assert.Contains("yes", confused.Errors[0].Message);
		}

		// Every default in the catalog survives the round trip through its own option: the defaults layer carries it as
		// TEXT, so a default the option's own parser cannot read back would be a setting that works until someone
		// passes it explicitly. This is what catches a default declared at the wrong width - `70000` as a port is an
		// int the catalog accepts and an Option<ushort> does not.
		[Fact]
		public void EverySettingsDefault_ParsesBackThroughItsOwnOption()
		{
			foreach (var option in FaolanSettings.All.Where(o => o.HasDefaultValue))
			{
				var result = new RootCommand("test") { option }.Parse([option.Name, FaolanSettings.DefaultText(option)!]);

				Assert.Empty(result.Errors);
				Assert.Equal(option.GetDefaultValue(), result.GetResult(option)!.GetValueOrDefault<object>());
			}
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
					Assert.Equal(FaolanSettings.DefaultText(setting), config[FaolanSettings.KeyOf(setting)], StringComparer.OrdinalIgnoreCase);

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

		// The `--help` text a command carrying `option` prints - the rendering under test, not a reconstruction of it.
		private static string Help(Option option)
		{
			var help = new StringWriter();
			new RootCommand("test") { option }.Parse(["--help"]).Invoke(new InvocationConfiguration { Output = help });

			return help.ToString();
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
				Keys.DatabaseProvider => config.DatabaseProvider,
				_ => throw new ArgumentException($"No accessor wired for '{key}' in this test.", nameof(key))
			};
		}
	}
}
