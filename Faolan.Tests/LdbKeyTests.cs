using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database;
using Faolan.Core.Protobuf;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The per-string LDB decryption key (docs/rdb/ldb-string-index.md): a 20-character base-85 rendering of a
	// 128-bit TEA key, sent by the server on every reference into an encrypted category.
	//
	// The round-trip gate is the point of this file. The client's decoder folds `v`..`z` down by 0x3c, which reads
	// like a many-to-one aliasing onto `:`..`>` and invites an encoder that emits the `:;<=>` form. It is not an
	// aliasing - the alphabet SKIPS 0x3a..0x3e - and the naive encoder round-trips only 47 of the 168 shipped keys
	// while looking correct on the other 47. Nothing but a whole-corpus round-trip catches that.
	public class LdbKeyTests
	{
		[Fact]
		public void Alphabet_Is85CharsAndSkipsTheXmlUnsafeRange()
		{
			Assert.Equal(85, LdbKey.Alphabet.Length);
			Assert.Equal(85, LdbKey.Alphabet.Distinct().Count());

			// `:;<=>` are absent - the key travels inside an XML attribute, and `<`/`>`/`=` cannot sit there raw.
			foreach (var c in ":;<=>")
				Assert.DoesNotContain(c, LdbKey.Alphabet);

			// ...and `vwxyz` stand in for those five digit values instead.
			foreach (var c in "vwxyz")
				Assert.Contains(c, LdbKey.Alphabet);
		}

		// The worked example from the decode: quest 10238's "Enter the jungle" goal.
		[Fact]
		public void Decode_MatchesTheClientsOwnDerivation()
		{
			var key = LdbKey.Decode("pARATX1!DX]\\So+0*A9B");
			Assert.NotNull(key);
			Assert.Equal(0xf6fe41fbu, key!.Key1);
			Assert.Equal(0xabb67cadu, key.Key2);
			Assert.Equal(0xbcdd972du, key.Key3);
			Assert.Equal(0x2f03abedu, key.Key4);
		}

		// The escaped wire form decodes identically - callers hold one or the other.
		[Theory]
		[InlineData("pARATX1!DX]\\So+0*A9B", "pARATX1!DX]&#92;So+0*A9B")]
		[InlineData("ECTdNAnx)l0$js$bDgG\"", "ECTdNAnx)l0&#36;js&#36;bDgG&quot;")]
		[InlineData("M1HK^'V*#T&%@Hmwdnf?", "M1HK^&#39;V*#T&amp;%@Hmwdnf?")]
		public void Decode_AcceptsTheEscapedWireForm(string raw, string escaped)
		{
			Assert.Equal(LdbKey.Decode(raw), LdbKey.Decode(escaped));
		}

		// ProtoDecryptionKey compares by VALUE - two instances carrying the same four words are equal, and a
		// different key is not. Without this the comparisons this type invites all silently ask about references.
		[Fact]
		public void Key_ComparesByValue()
		{
			var a = LdbKey.Decode("pARATX1!DX]\\So+0*A9B")!;
			var b = LdbKey.Decode("pARATX1!DX]\\So+0*A9B")!;
			var other = LdbKey.Decode("ECTdNAnx)l0$js$bDgG\"")!;

			Assert.False(ReferenceEquals(a, b));
			Assert.Equal(a, b);
			Assert.True(a == b);
			Assert.Equal(a.GetHashCode(), b.GetHashCode());
			Assert.NotEqual(a, other);
			Assert.True(a != other);
			Assert.False(a.IsEmpty);
			Assert.True(new ProtoDecryptionKey().IsEmpty);
		}

		// An escaped '&' must not re-introduce the other entities when unescaping.
		[Fact]
		public void EscapeUnescape_RoundTripsIncludingAmpersand()
		{
			const string raw = "M1HK^'V*#T&%@Hmwdnf?";
			var escaped = LdbKey.Escape(raw);
			Assert.Equal("M1HK^&#39;V*#T&amp;%@Hmwdnf?", escaped);
			Assert.Equal(raw, LdbKey.Unescape(escaped));
		}

		[Fact]
		public void Decode_RejectsMalformedKeys()
		{
			Assert.Null(LdbKey.Decode(null));
			Assert.Null(LdbKey.Decode(""));
			Assert.Null(LdbKey.Decode("tooshort"));
			Assert.Null(LdbKey.Decode("pARATX1!DX]:So+0*A9B")); // ':' is outside the alphabet
		}

		// ⚠️ THE gate: every key the seed ships must survive key -> 20-char wire string -> key. The stored form is now
		// the four words, so this is the direction that matters at send time - a rendering the client cannot decode,
		// or one that decodes to a DIFFERENT key, silently blanks the text it addresses.
		//
		// The naive encoder (emitting `:;<=>` for digits 25..29 instead of `vwxyz`) fails this on ~72% of real keys
		// while looking correct on the rest, which is why it is a whole-corpus gate and not a handful of samples.
		[Fact]
		public async Task EveryShippedKey_RoundTripsExactly()
		{
			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var ct = TestContext.Current.CancellationToken;

			var keys = (await db.QuestGoalDefinitions.AsNoTracking().Select(g => g.ObjectiveKey).ToListAsync(ct))
				.Concat(await db.QuestGoalDefinitions.AsNoTracking().Select(g => g.TrailingKey).ToListAsync(ct))
				.Concat(await db.QuestTextRefs.AsNoTracking().Select(r => r.KeyText).ToListAsync(ct))
				.Concat(await db.QuestLines.AsNoTracking().Select(a => a.TitleKey).ToListAsync(ct))
				.Concat(await db.QuestLines.AsNoTracking().Select(a => a.MapKey).ToListAsync(ct))
				.Concat(await db.QuestLines.AsNoTracking().Select(a => a.RegionKey).ToListAsync(ct))
				// ...and the DIALOGUE half of the encrypted set (categories 50000-50003): conversation screens and
				// their parameters, stock phrases, NPC barks, world-object display text.
				.Concat(await db.ConversationScreens.AsNoTracking().Select(c => c.Key).ToListAsync(ct))
				.Concat(await db.ConversationScreenParameters.AsNoTracking().Select(p => p.ContentKey).ToListAsync(ct))
				.Concat(await db.StockPhrases.AsNoTracking().Select(p => p.FormatKey).ToListAsync(ct))
				.Concat(await db.NpcChatter.AsNoTracking().Select(c => c.FormatKey).ToListAsync(ct))
				.Concat(await db.WorldObjects.AsNoTracking().Select(o => o.DisplayKey).ToListAsync(ct))
				// ...and the zone-line travel confirmation box (#637), which is category 520 and so not encrypted at all -
				// it is here because the round-trip gate is about the KEY ENCODING, which is the same for every category.
				.Concat(await db.Maps.AsNoTracking().Select(m => m.TravelConfirmKey).ToListAsync(ct))
				.Where(k => k != null && !k.IsEmpty)
				.Distinct()
				.ToList();

			Assert.True(keys.Count > 5000, $"only {keys.Count} keys in the seed - the gate would be vacuous");

			var broken = keys.Where(k => LdbKey.Decode(LdbKey.ToWire(k)) != k).ToList();
			Assert.True(broken.Count == 0, $"{broken.Count} of {keys.Count} keys do not round-trip, e.g. {broken.FirstOrDefault()}");

			// Every rendering is a well-formed 20-character key drawn from the alphabet - what the client asserts.
			Assert.All(keys, k =>
			{
				var raw = LdbKey.Encode(k);
				Assert.Equal(LdbKey.KeyLength, raw.Length);
				Assert.All(raw, c => Assert.Contains(c, LdbKey.Alphabet));
			});
		}
	}
}
