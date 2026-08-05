using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Rdb;

namespace Faolan.Importer.Extractors
{
	// The client's own ambient-BARK line catalog: LDB category 50015 (`sayinchat`), read straight out of the RDB
	// (#331).
	//
	// Two properties make this readable at all, and both are the opposite of what the neighbouring subsystems do:
	//
	//   * the category is PLAINTEXT (page `flags=0`), unlike the eleven encrypted ones, so a line needs no key. The
	//     20-character `key=` a captured bark carries is inert here - the client's decrypt is guarded on the string's
	//     own encrypted flag, so it never reads a key on a flags=0 page (`docs/rdb/ldb-string-index.md`). A HARVESTED
	//     line is therefore servable without a capture of it.
	//   * a line's string id is a pure function of its TOKEN - the client's 28-bit ELF hash of
	//     `sayinchat(N)|<channel>(K)|<Group>` - so a group's whole line set is enumerable by sweeping N rather than
	//     waiting for a capture of each line.
	//
	// ⚠️ This CANNOT discover group names, and the obvious way to try does not work: see `docs/subsystems/chat.md`.
	// Sweeping is only useful for a group some capture has already named.
	public sealed class BarkCatalog
	{
		// `sayinchat` - the category every captured bark names in its `category=` attribute.
		public const uint Category = 50015;

		// How far a per-group sweep walks `sayinchat(N)`. Empirical headroom: the highest N any capture carries is 57
		// (Tatianus the Crier), and each candidate costs one hash plus one dictionary probe.
		public const uint SweepCeiling = 512;

		// A bark's template slot - `{TopGloryName}`, `{DetectedChar.name}`, `{AdvertiseMinutes - AdvertiseCounter}`.
		// A line carrying one cannot simply be served: its wire form needs a matching <parameter> element whose value
		// comes from live server state, which is separate work from harvesting the TEXT.
		private static readonly Regex SlotRx = new Regex("\\{[^}]+\\}", RegexOptions.Compiled);

		private readonly Dictionary<uint, byte[]> _blobs;

		private BarkCatalog(int pages, IReadOnlySet<uint> flags, Dictionary<uint, byte[]> blobs)
		{
			Pages = pages;
			Flags = flags;
			_blobs = blobs;
		}

		public int Pages { get; }

		// The page header's encryption flags, as read. Carried rather than asserted so a client build that ever ships
		// this category encrypted reports that instead of silently yielding nothing.
		public IReadOnlySet<uint> Flags { get; }

		public bool IsPlaintext => !Flags.Contains(1);

		public int StringCount => _blobs.Count;

		public static BarkCatalog Load(string rdbDir)
		{
			using var rdb = new RdbReader(rdbDir);
			var language = LdbLanguageIndex.LanguageByPage(rdb);
			var blobs = new Dictionary<uint, byte[]>();
			var flags = new HashSet<uint>();
			var pages = 0;

			foreach (var e in rdb.EntriesOfType(LdbLanguageIndex.PageType).Where(x => x.IsLocal))
			{
				if (!language.TryGetValue(e.Id, out var lang) || lang != LdbLanguageIndex.English)
					continue;

				byte[] body;
				try
				{
					body = rdb.ReadDecompressed(e);
				}
				catch
				{
					continue; // not a readable record (CDN-only / not zlib)
				}

				if (LdbStringIndex.CategoryOf(body) != Category)
					continue;

				pages++;
				flags.Add(LdbStringIndex.FlagsOf(body));
				foreach (var entry in LdbStringIndex.ParseRaw(body))
					blobs[entry.Id] = entry.Bytes;
			}

			return new BarkCatalog(pages, flags, blobs);
		}

		/// <summary>The line a bark token addresses, or null when the client ships none.</summary>
		public string? Line(uint sayInChat, string channel, uint channelCount, string group)
		{
			return Text(LdbStringIndex.TokenId($"sayinchat({sayInChat})|{channel}({channelCount})|{group}"));
		}

		/// <summary>Every line the client ships for one (group, channel) - the group's whole conversation.</summary>
		public IEnumerable<(uint SayInChat, string Text)> Lines(string channel, uint channelCount, string group)
		{
			for (var n = 0u; n <= SweepCeiling; n++)
			{
				if (Line(n, channel, channelCount, group) is { Length: > 0 } line)
					yield return (n, line);
			}
		}

		/// <summary>Whether a line needs a <c>&lt;parameter&gt;</c> filled from live server state.</summary>
		public static bool HasSlot(string line)
		{
			return SlotRx.IsMatch(line ?? "");
		}

		public static IReadOnlyList<string> SlotsOf(string line)
		{
			return SlotRx.Matches(line ?? "").Select(m => m.Value).Distinct().ToList();
		}

		// The page's string: NUL-truncated, UTF-8. Null when the id is absent or the bytes are not valid UTF-8 - the
		// latter would mean the category is not plaintext after all, which `IsPlaintext` reports properly.
		public string? Text(uint id)
		{
			if (!_blobs.TryGetValue(id, out var blob))
				return null;

			var end = Array.IndexOf(blob, (byte)0);
			try
			{
				return new UTF8Encoding(false, true).GetString(blob.AsSpan(0, end < 0 ? blob.Length : end));
			}
			catch (DecoderFallbackException)
			{
				return null;
			}
		}
	}
}
