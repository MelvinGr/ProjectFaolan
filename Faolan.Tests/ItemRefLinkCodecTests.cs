using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Text;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Faolan.Tests
{
	// The `itemref://` chat link - a third source of an item's per-item TEA key, and the only one that reaches
	// items the capturing character never held (a stranger's trade-channel link).
	//
	// The strings below are verbatim from the corpus, one per shape the 43 captured links take. They are literals
	// rather than a fixture because they are the grammar: a link is short, printable, and the thing under test is
	// how it is spelled.
	public class ItemRefLinkCodecTests
	{
		// The ordinary shape: template1 == template2, one key repeated three times, an 80th-level trade link.
		private const string Plain =
			"<a style=\"text-decoration:none\" href=\"itemref://5077852/5077852/0/80/4062f862:18dd164e:ad6d38cc:364bc05e/4062f862:18dd164e:ad6d38cc:364bc05e/4062f862:18dd164e:ad6d38cc:364bc05e\"><font color=#8d11be>[White Hand Gem of Lightning Cloak]</font></a>";

		// A level-scaled pair: template2 is a different template with its own key, and key3 is key1 again.
		private const string Pair =
			"<a style=\"text-decoration:none\" href=\"itemref://3983088/3983089/0/75/72e2660e:e30229bd:b2019308:dc6aefd/1b3e7a1d:af9866bb:7a736b7:4e022e23/72e2660e:e30229bd:b2019308:dc6aefd\"><font color=#6ea7>[Sanguine Radiant Sapphire]</font></a>";

		// key3 is the NullIdentity sentinel (a pre-existence item), and the name carries an apostrophe.
		private const string NullIdentityKey3 =
			"<a style=\"text-decoration:none\" href=\"itemref://4257867/4257867/0/1/45e19f7:f115ee9b:7f7f8af6:480e5fd8/45e19f7:f115ee9b:7f7f8af6:480e5fd8/b290805c:29e627ca:b290805c:29e627ca\"><font color=#8a8a89>[Commander's Haversack]</font></a>";

		[Theory]
		[InlineData(Plain)]
		[InlineData(Pair)]
		[InlineData(NullIdentityKey3)]
		public void EveryCapturedShape_RebuildsCharacterForCharacter(string link)
		{
			// The unpadded lowercase hex is the trap: `45e19f7` is seven characters and `#6ea7` is four, so a build
			// that zero-pads either does not reproduce what the client sent.
			Assert.Equal(link, ItemRefLinkCodec.Build(ItemRefLinkCodec.Parse(link)));
		}

		[Fact]
		public void ThePlainLink_YieldsTheIdentityWholeAndStatesNoPair()
		{
			var link = ItemRefLinkCodec.Parse(Plain);

			Assert.Equal(5077852u, link.TemplateId);
			Assert.Equal(80u, link.Level);
			Assert.Equal(new ItemRefKey(0x4062F862, 0x18DD164E, 0xAD6D38CC, 0x364BC05E), link.Key1);
			Assert.Equal("White Hand Gem of Lightning Cloak", link.Name);
			Assert.False(link.HasPair); // template2 == template1 says nothing
		}

		[Fact]
		public void APairLink_NamesASecondTemplateWithItsOwnKey()
		{
			var link = ItemRefLinkCodec.Parse(Pair);

			Assert.True(link.HasPair);
			Assert.Equal(3983089u, link.TemplateId2);
			Assert.NotEqual(link.Key1, link.Key2); // the high endpoint is encrypted under its own key
		}

		// Read key1, never key3.
		[Fact]
		public void TheNullIdentitySentinel_IsCarriedInKey3AndIsNotAKey()
		{
			var link = ItemRefLinkCodec.Parse(NullIdentityKey3);

			Assert.Equal(ItemRefLink.NullIdentity, link.Key3);
			Assert.NotEqual(ItemRefLink.NullIdentity, link.Key1);
		}

		// A chat link is player-typed text reaching an importer, so a shape we cannot rebuild is refused outright
		// rather than half-read - half-reading one would put an invented key on an item row.
		[Theory]
		[InlineData("")]
		[InlineData("<a href=\"http://example.invalid\">[Not an item]</a>")]
		[InlineData("<a style=\"text-decoration:none\" href=\"itemref://5077852/5077852/0/80/4062f862:18dd164e\"><font color=#8d11be>[Truncated]</font></a>")]
		public void AnythingThatIsNotTheAnchor_Throws(string text)
		{
			Assert.Throws<CodecParseException>(() => ItemRefLinkCodec.Parse(text));
		}

		// The oracle for the whole source, and it is non-circular: the key came from the link, never from the
		// database. Every linked item we already hold must agree with the link on its stored quad - if a link could
		// disagree with a snapshot-sourced row, nothing it says about the twelve items we don't hold would be worth
		// importing.
		[Theory]
		[InlineData(Plain, false)] // one of the twelve the link added - present now, so it must agree too
		[InlineData(Pair, true)]
		[InlineData(NullIdentityKey3, false)]
		public async Task ALinkedItemWeHold_AgreesWithTheStoredKeyQuad(string text, bool expectPairStored)
		{
			var link = ItemRefLinkCodec.Parse(text);

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var item = await db.Items.AsNoTracking().SingleAsync(i => i.Id == link.TemplateId, TestContext.Current.CancellationToken);

			Assert.Equal(link.Key1, new ItemRefKey(item.Key.Key1, item.Key.Key2, item.Key.Key3, item.Key.Key4));
			Assert.Equal(link.Name, item.Name);
			Assert.Equal(link.Level, item.Level);
			Assert.Equal(expectPairStored, item.TemplateId2 == link.TemplateId2);
		}

		// The twelve templates the corpus's links added, none of which any other source ever saw. A regression on
		// this is a regression on the breadth a link buys over a snapshot.
		[Fact]
		public async Task TheChatLinkOnlyTemplates_AreInTheCatalogWithARealKey()
		{
			uint[] linkOnly =
			[
				3769384, 3875215, 4257867, 4325762, 4503110, 4887856, 5064223, 5064267, 5073855, 5077493, 5077828, 5077852
			];

			await using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var rows = await db.Items.AsNoTracking().Where(i => linkOnly.Contains(i.Id)).ToListAsync(TestContext.Current.CancellationToken);

			Assert.Equal(linkOnly.Length, rows.Count);
			Assert.All(rows, r =>
			{
				Assert.NotEqual(0u, r.Key.Key1); // without the key the RDB template cannot be decrypted at all
				Assert.NotEmpty(r.Name);
				Assert.NotEqual(0, r.Level);
				// A link carries no hash word, and that is fine: AcgHash.ForWire mints one at send time. The 0
				// is the provenance record saying this identity came from a link.
				Assert.Equal(0u, r.Hash);
			});
		}
	}
}
