using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Cli;
using Faolan.Core.Enums;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Faolan.Tests
{
	// The `Realms` configuration section: the STATIC half of a Realm row, declared in faolan.json instead of edited
	// into a committed binary database by hand.
	//
	// ⚠️ Most of these gates are about the ways a realm can be declared WRONG, because the cost of each is paid at
	// the far end of a login rather than at startup: a realm the login tier advertises with a port nothing binds is a
	// client that connects, waits, and gives up. Config time is the only place left to catch them where the message
	// can still name the key.
	public class DeclaredRealmTests
	{
		[Fact]
		// The bottom layer, and the reason a checkout that has never heard of this section still comes up: nothing
		// declared means the built-in default, which is the historical single-process realm.
		public void NothingDeclared_ReadsBackAsTheBuiltInDefault()
		{
			var config = Config([]);

			Assert.False(FaolanRealms.IsDeclared(config));
			Assert.Equal(FaolanRealms.Default, FaolanRealms.Declared(config));
		}

		[Fact]
		public void ADeclaredRealm_ParsesEveryField()
		{
			var config = Config(Realm(7, name: "Fury", ip: "10.0.0.4", port: 8014, chat: 8012, cs: 8001, type: "PvE", country: "German"));

			var realm = Assert.Single(FaolanRealms.Declared(config));

			Assert.Equal(new DeclaredRealm(7, "Fury", "10.0.0.4", 8014, 8012, 8001, RealmType.PvE, RealmCountry.German), realm);
		}

		[Fact]
		// An enum is spelled by NAME in the file (the generator writes "PvP", not 256), and case-insensitively, the
		// same way the catalog's own enum settings parse.
		public void AnEnum_IsSpelledByNameAndIsCaseInsensitive()
		{
			var realm = Assert.Single(FaolanRealms.Declared(Config(Realm(1, type: "pvpbyfaction", country: "FRENCH"))));

			Assert.Equal(RealmType.PvPByFaction, realm.Type);
			Assert.Equal(RealmCountry.French, realm.Country);
		}

		[Theory]
		// There is no per-field default: a half-declared realm is one the login tier would advertise wrongly, so every
		// field is named or the realm is refused. The message has to carry the KEY, since the operator's next move is
		// to go and fix that line.
		[InlineData("Name")]
		[InlineData("IpAddress")]
		[InlineData("Port")]
		[InlineData("ChatServerPort")]
		[InlineData("CsPlayerAgentPort")]
		[InlineData("Type")]
		[InlineData("Country")]
		public void AMissingField_IsRefusedByName(string field)
		{
			var realm = Realm(1);
			realm.Remove($"Realms:0:{field}");

			Assert.Contains($"'Realms:0:{field}'", Throws(realm));
		}

		[Fact]
		// ⭐ The id is the foreign key every character, guild, mail and auction listing carries, so it cannot be
		// implicit. 0 is what an omitted or unparsable value reads back as, AND what the database takes as "generate
		// one" - so it is refused rather than defaulted.
		public void AZeroOrMissingId_IsRefused()
		{
			Assert.Contains("'Realms:0:Id'", Throws(Realm(0)));

			var missing = Realm(1);
			missing.Remove("Realms:0:Id");
			Assert.Contains("'Realms:0:Id'", Throws(missing));
		}

		[Theory]
		// Port 0 parses fine as a ushort. It means "let the kernel pick" to a bind and "nothing is listening" to the
		// client the login tier hands it to, so the realm would come up looking healthy and route players nowhere.
		[InlineData("Port")]
		[InlineData("ChatServerPort")]
		[InlineData("CsPlayerAgentPort")]
		public void APortOfZero_IsRefused(string field)
		{
			var realm = Realm(1);
			realm[$"Realms:0:{field}"] = "0";

			Assert.Contains($"'Realms:0:{field}' is 0", Throws(realm));
		}

		[Fact]
		// The same check Realm.IpAddressLong makes (#845: the chat/CS handoff carries a u32 and the client is IPv4
		// only), moved to config time - it used to throw on the SEND, i.e. after a player had already picked the realm.
		public void AnIpV6Address_IsRefusedAtConfigTime()
		{
			Assert.Contains("InterNetworkV6", Throws(Realm(1, ip: "::1")));
		}

		[Fact]
		public void ANonAddress_IsRefused()
		{
			Assert.Contains("not an IP address", Throws(Realm(1, ip: "realm-two.example.com")));
		}

		[Fact]
		public void AnUnknownEnumValue_IsRefusedAndNamesTheChoices()
		{
			var message = Throws(Realm(1, type: "PvPish"));

			Assert.Contains("'Realms:0:Type'", message);
			Assert.Contains("PvPByFaction", message);
		}

		[Fact]
		// Two declarations of one id fight over one row: whichever the reconciler writes last wins, silently.
		public void TwoRealmsWithOneId_AreRefused()
		{
			Assert.Contains("id 3", Throws(Realm(3), Realm(3, name: "Other", port: 9014, chat: 9012, cs: 9001)));
		}

		[Fact]
		// The database has a unique index on the name, so this would fail at the write instead - and the client shows
		// nothing else to pick between two realms.
		public void TwoRealmsWithOneName_AreRefused()
		{
			Assert.Contains("named 'Crom'", Throws(Realm(1, name: "Crom"), Realm(2, name: "Crom", port: 9014, chat: 9012, cs: 9001)));
		}

		[Fact]
		// One host binds all three, so a realm that declares the same port twice cannot start.
		public void ARealmReusingItsOwnPort_IsRefused()
		{
			Assert.Contains("port 7014 twice", Throws(Realm(1, port: 7014, chat: 7014)));
		}

		[Fact]
		// ⭐ #910 again, one layer up: there it was one global MCP port shared by every realm process, here it is two
		// realm hosts racing for a bind. The loser dies with the winner already advertised, which is the quiet,
		// wrong-direction failure the liveness work exists to remove.
		public void TwoRealmsSharingAPortOnOneAddress_AreRefused()
		{
			Assert.Contains("both declare port 7012", Throws(Realm(1), Realm(2, name: "Other", port: 9014, chat: 7012, cs: 9001)));
		}

		[Fact]
		// ...but the SAME ports on a DIFFERENT machine are the normal multi-realm shape, and must stay legal.
		public void TwoRealmsWithTheSamePortsOnDifferentAddresses_AreFine()
		{
			var realms = FaolanRealms.Declared(Config(Realm(1, ip: "10.0.0.1"), Realm(2, name: "Other", ip: "10.0.0.2")));

			Assert.Equal([1u, 2u], realms.Select(r => r.Id));
		}

		private static IConfiguration Config(params Dictionary<string, string?>[] realms)
		{
			var values = new Dictionary<string, string?>();
			for (var i = 0; i < realms.Length; i++)
			{
				foreach (var (key, value) in realms[i])
					values[key.Replace(":0:", $":{i}:", StringComparison.Ordinal)] = value;
			}

			return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
		}

		// One realm's keys, at index 0 - Config renumbers them when several are passed, so each case below reads as
		// the realm it declares rather than as an index.
		private static Dictionary<string, string?> Realm(uint id, string name = "Test", string ip = "127.0.0.1", ushort port = 7014,
			ushort chat = 7012, ushort cs = 7001, string type = "PvP", string country = "English")
		{
			return new Dictionary<string, string?>
			{
				["Realms:0:Id"] = id.ToString(),
				["Realms:0:Name"] = name,
				["Realms:0:IpAddress"] = ip,
				["Realms:0:Port"] = port.ToString(),
				["Realms:0:ChatServerPort"] = chat.ToString(),
				["Realms:0:CsPlayerAgentPort"] = cs.ToString(),
				["Realms:0:Type"] = type,
				["Realms:0:Country"] = country
			};
		}

		private static string Throws(params Dictionary<string, string?>[] realms)
		{
			return Assert.Throws<InvalidOperationException>(() => FaolanRealms.Declared(Config(realms))).Message;
		}
	}
}
