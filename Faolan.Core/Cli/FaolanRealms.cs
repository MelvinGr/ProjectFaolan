using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Cli
{
	/// <summary>
	///     A realm as the OPERATOR declares it - the static half of a <c>Realm</c> row, read from the configuration
	///     stack instead of being edited into the database by hand.
	/// </summary>
	/// <remarks>
	///     ⚠️ Deliberately not the <c>Realm</c> entity, and deliberately not all of it. A realm row is three things
	///     wearing one name: this static deployment config, the LIVE columns (<c>OnlineStatus</c> and
	///     <c>LastSeenUtc</c>, written only by the host that serves the realm, plus <c>LoadStatus</c>, which is the
	///     realm's population), and the parent of four foreign keys. Only the first can be declared - a file cannot
	///     tell the login tier that a host on another machine just died, cannot count who is logged in, and a row that
	///     nothing points at cannot anchor <c>Character.RealmId</c>. So
	///     this is reconciled INTO the table rather than replacing it (see <c>DatabaseInitializer.ReconcileRealms</c>).
	///     <para />
	///     <see cref="Id" /> is explicit and required for the same reason: it is a persisted foreign key. An implicit
	///     ordinal would silently repoint every character, guild, mail and auction listing on the realm the day
	///     somebody reordered the file.
	/// </remarks>
	public sealed record DeclaredRealm(
		uint Id,
		string Name,
		string IpAddress,
		ushort Port,
		ushort ChatServerPort,
		ushort CsPlayerAgentPort,
		RealmType Type,
		RealmLanguage Language);

	/// <summary>
	///     The <c>Realms</c> configuration section: the realms this deployment declares, and the built-in default when
	///     it declares none.
	/// </summary>
	/// <remarks>
	///     ⚠️ This is the one part of faolan.json that is NOT in <see cref="FaolanSettings" />. That catalog is a flat
	///     list of scalars with one accessor per key, one command-line option per key, and a test pinning it in key
	///     order; a LIST of records fits none of that. Rather than bend the catalog into holding arrays - which would
	///     mean `--Realms:0:Port` as a command-line option and an accessor per field per realm - the section is read
	///     here, and <c>FaolanSettingsTests</c> exempts it by name.
	///     <para />
	///     Everything else about it behaves the way the catalog does: the values below are the bottom layer, the
	///     committed faolan.json is GENERATED from them, and a later layer (faolan.user.json, the command line, the
	///     environment) overrides. What it does not do is MERGE - see <see cref="Declared" />.
	/// </remarks>
	public static class FaolanRealms
	{
		public const string Section = "Realms";

		/// <summary>
		///     The realm a deployment gets when it declares none - the historical single-process ports, so a checkout
		///     that has never heard of this section comes up exactly as it did before.
		/// </summary>
		public static readonly ImmutableArray<DeclaredRealm> Default =
		[
			new DeclaredRealm(1, "Project Faolan", "127.0.0.1", 7014, 7012, 7001, RealmType.PvP, RealmLanguage.English)
		];

		/// <summary>
		///     Whether any configuration layer declares realms at all.
		/// </summary>
		/// <remarks>
		///     ⭐ This is what keeps every existing deployment byte-identical. An UNDECLARED deployment is left alone
		///     entirely: the reconciler falls back to the old "insert the default realm if the table is empty, else do
		///     nothing" behaviour, so a database whose realm happens to carry some other id is never duplicated by a
		///     declaration it never made. Config becomes authoritative only once somebody writes it down.
		/// </remarks>
		public static bool IsDeclared(IConfiguration config)
		{
			return config.GetSection(Section).GetChildren().Any();
		}

		/// <summary>
		///     The declared realms, validated - or <see cref="Default" /> when nothing declares any.
		/// </summary>
		/// <remarks>
		///     ⚠️ A configuration layer REPLACES an array element, it does not merge arrays: `Realms:1:Port` in
		///     faolan.user.json overrides that one key of the second realm and leaves the rest of the committed entry
		///     standing, but there is no way to REMOVE an entry a lower layer declared. So a deployment that wants a
		///     different set of realms declares the whole set, and the generated faolan.json declares exactly one so
		///     there is little to inherit by accident.
		/// </remarks>
		public static ImmutableArray<DeclaredRealm> Declared(IConfiguration config)
		{
			var children = config.GetSection(Section).GetChildren().ToArray();
			if (children.Length == 0)
				return Default;

			var realms = children.Select(Read).ToImmutableArray();
			Validate(realms);

			return realms;
		}

		/// <summary>
		///     The <c>Realms</c> block of the generated faolan.json, rendered from <see cref="Default" /> so the
		///     committed file and the built-in default cannot disagree.
		/// </summary>
		internal static List<string> DefaultJsonLines()
		{
			var lines = new List<string>
			{
				"\t// The realms this deployment serves - the STATIC half of each Realm row, reconciled into the database at",
				"\t// startup. The LIVE columns are not here and cannot be declared: Online status and the heartbeat are written",
				"\t// by the host that serves the realm, and LoadStatus is its population.",
				"\t// Ids are explicit and must stay stable: every character, guild, mail and auction listing is FK'd to one.",
				"\t// Declare the whole set - a later layer overrides an entry's keys but cannot remove an entry.",
				$"\t\"{Section}\": ["
			};

			for (var i = 0; i < Default.Length; i++)
			{
				var realm = Default[i];
				lines.Add("\t\t{");
				lines.Add($"\t\t\t\"Id\": {realm.Id.ToString(CultureInfo.InvariantCulture)},");
				lines.Add($"\t\t\t\"Name\": {JsonSerializer.Serialize(realm.Name)},");
				lines.Add($"\t\t\t\"IpAddress\": {JsonSerializer.Serialize(realm.IpAddress)},");
				lines.Add($"\t\t\t\"Port\": {realm.Port.ToString(CultureInfo.InvariantCulture)},");
				lines.Add($"\t\t\t\"ChatServerPort\": {realm.ChatServerPort.ToString(CultureInfo.InvariantCulture)},");
				lines.Add($"\t\t\t\"CsPlayerAgentPort\": {realm.CsPlayerAgentPort.ToString(CultureInfo.InvariantCulture)},");
				lines.Add($"\t\t\t\"Type\": {JsonSerializer.Serialize(realm.Type.ToString())},");
				lines.Add($"\t\t\t\"Language\": {JsonSerializer.Serialize(realm.Language.ToString())}");
				lines.Add(i == Default.Length - 1
					? "\t\t}"
					: "\t\t},");
			}

			lines.Add("\t]");

			return lines;
		}

		// The realm's own IPv4 check, run at CONFIG time rather than at wire-write time. Realm.IpAddressLong throws
		// the same way (#845: the chat/CS handoff carries a u32 and the client is IPv4-only), but it throws on the
		// send - i.e. once a player has already picked the realm and is waiting for a handoff that will never come.
		private static string Address(IConfigurationSection realm, string key)
		{
			var text = Text(realm, key);
			if (!IPAddress.TryParse(text, out var address))
				throw new InvalidOperationException($"{Where(realm, key)} is '{text}', which is not an IP address.");

			return address.AddressFamily == AddressFamily.InterNetwork
				? text
				: throw new InvalidOperationException(
					$"{Where(realm, key)} is '{text}', which is {address.AddressFamily}. The chat and CS handoffs carry a u32 IPv4 address and the retail client is IPv4-only (#845), so a realm must have a v4 address.");
		}

		private static T Choice<T>(IConfigurationSection realm, string key) where T : struct, Enum
		{
			var text = Text(realm, key);

			return Enum.TryParse<T>(text, true, out var value) && Enum.IsDefined(value)
				? value
				: throw new InvalidOperationException($"{Where(realm, key)} is '{text}', which is not one of {Enum.GetValues<T>().JoinAsString(", ")}.");
		}

		// ⚠️ The three ports are read as ushort but rejected at ZERO, which uint16 parsing accepts. Port 0 means "let
		// the kernel pick" to a bind and "nothing is listening" to the client the login tier hands it to - so it would
		// come up looking healthy and route every player into a black hole.
		private static ushort Port(IConfigurationSection realm, string key)
		{
			var port = Number<ushort>(realm, key);

			return port != 0
				? port
				: throw new InvalidOperationException($"{Where(realm, key)} is 0. A realm's ports are BOUND by its host and ADVERTISED to clients, so each one has to be a real port.");
		}

		private static T Number<T>(IConfigurationSection realm, string key) where T : IParsable<T>
		{
			var text = Text(realm, key);

			return T.TryParse(text, CultureInfo.InvariantCulture, out var value)
				? value
				: throw new InvalidOperationException($"{Where(realm, key)} is '{text}', which is not a {typeof(T).Name}.");
		}

		private static DeclaredRealm Read(IConfigurationSection realm)
		{
			// The id is the FK every realm-scoped row carries, so 0 - what an omitted or unparsable value would read
			// back as - is refused rather than defaulted. It is also what the database would take as "generate one".
			var id = Number<uint>(realm, nameof(DeclaredRealm.Id));
			if (id == 0)
				throw new InvalidOperationException($"{Where(realm, nameof(DeclaredRealm.Id))} is 0. A realm's id is the foreign key its characters, guilds, mail and auction listings carry, so it has to be explicit and stable.");

			var name = Text(realm, nameof(DeclaredRealm.Name));
			if (name.Length is 0 or > 128)
				throw new InvalidOperationException($"{Where(realm, nameof(DeclaredRealm.Name))} is {(name.Length == 0 ? "empty" : $"{name.Length} characters")}; a realm name is 1 to 128 characters.");

			return new DeclaredRealm(
				id,
				name,
				Address(realm, nameof(DeclaredRealm.IpAddress)),
				Port(realm, nameof(DeclaredRealm.Port)),
				Port(realm, nameof(DeclaredRealm.ChatServerPort)),
				Port(realm, nameof(DeclaredRealm.CsPlayerAgentPort)),
				Choice<RealmType>(realm, nameof(DeclaredRealm.Type)),
				Choice<RealmLanguage>(realm, nameof(DeclaredRealm.Language)));
		}

		private static string Text(IConfigurationSection realm, string key)
		{
			return realm[key] is { Length: > 0 } text
				? text
				: throw new InvalidOperationException(
					$"{Where(realm, key)} has no value. A declared realm names every field - there is no per-field default, because a half-declared realm is a realm the login tier would advertise wrongly.");
		}

		// ⭐ The cross-realm checks are the ones worth having. A duplicate id silently makes two declarations fight
		// over one row; a repeated port on one address is #910 again, except now it is two realm HOSTS racing for a
		// bind and the loser dying with the winner already advertised.
		private static void Validate(ImmutableArray<DeclaredRealm> realms)
		{
			if (Duplicate(realms.Select(r => r.Id.ToString(CultureInfo.InvariantCulture))) is { } id)
				throw new InvalidOperationException($"Two realms in '{Section}' are declared with id {id}. A realm id identifies one row and one set of ports.");

			if (Duplicate(realms.Select(r => r.Name), StringComparer.OrdinalIgnoreCase) is { } name)
				throw new InvalidOperationException($"Two realms in '{Section}' are named '{name}'. Realm names are unique - the database has a unique index on them, and the client shows nothing else to pick between them.");

			foreach (var realm in realms)
			{
				if (Duplicate([realm.Port.ToString(CultureInfo.InvariantCulture), realm.ChatServerPort.ToString(CultureInfo.InvariantCulture), realm.CsPlayerAgentPort.ToString(CultureInfo.InvariantCulture)]) is { } port)
					throw new InvalidOperationException($"Realm {realm.Id} ('{realm.Name}') declares port {port} twice. Its world, chat and CS listeners each bind their own.");
			}

			foreach (var host in realms.GroupBy(r => r.IpAddress, StringComparer.OrdinalIgnoreCase))
			{
				var ports = host.SelectMany(r => new[] { r.Port, r.ChatServerPort, r.CsPlayerAgentPort }).Select(p => p.ToString(CultureInfo.InvariantCulture));
				if (Duplicate(ports) is { } shared)
					throw new InvalidOperationException(
						$"Two realms on {host.Key} both declare port {shared}. Realm hosts bind their own ports, so the second one to start would die on 'address already in use' with the first already advertised.");
			}
		}

		private static string? Duplicate(IEnumerable<string> values, IEqualityComparer<string>? comparer = null)
		{
			var seen = new HashSet<string>(comparer ?? StringComparer.Ordinal);

			return values.FirstOrDefault(value => !seen.Add(value));
		}

		private static string Where(IConfigurationSection realm, string key)
		{
			return $"'{Section}:{realm.Key}:{key}'";
		}
	}
}
