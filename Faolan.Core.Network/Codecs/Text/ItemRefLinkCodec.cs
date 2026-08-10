using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Text
{
	// The `itemref://` CHAT LINK - what the client emits when a player links an item into chat (#751). A rule-1a
	// STRING codec (#530): `string Build(ItemRefLink)` / `ItemRefLink Parse(string)`.
	//
	// It matters because it is a THIRD source of the per-item TEA key, and one of a different kind: the inventory
	// snapshot and `DispatchAddItem` only ever describe items the capturing character HELD, where a trade-channel
	// link describes whatever a stranger was selling. Without a key an item's RDB template cannot be decrypted at
	// all (#203), so a link is not an id+name stub - it unlocks the whole template.
	//
	// The element is the anchor entire, because that is where the NAME is:
	//
	//   <a style="text-decoration:none" href="itemref://T1/T2/T3/LEVEL/KEY1/KEY2/KEY3"><font color=#RGB>[Name]</font></a>
	//
	// so one link yields `Items.Id`, `Name`, `Level` and the `Key1..Key4` quad outright. The path is the whole
	// `ProtoAcgItem` in plain text with ONE field dropped: there is no `hash` word, unlike the loot-chat body form
	// (`[template][template][0][level][hash][k1][k2][k3]`). That is fine - #723 mints a token at send time and
	// `Items.Hash == 0` means "not harvested".
	//
	// ⚠️ Read KEY1. `key3` is sometimes the `NullIdentity` sentinel `b290805c:29e627ca:b290805c:29e627ca` (both of
	// the corpus sightings are pre-existence items), and `key2` belongs to the SECOND template of a level-scaled
	// pair (#752) - equal to key1 on everything that is not one.
	//
	// ⚠️ Both the key words and the colour are printed as UNPADDED lowercase hex - `45e19f7` is seven characters and
	// `#6ea7` is four - so a build that zero-pads them does not reproduce the bytes.
	public static class ItemRefLinkCodec
	{
		// The marker that says an anchor is one of these. Public because finding candidates in a chat body is a SCAN
		// rather than a decode: the caller locates the anchors, this codec decides what each one says.
		public const string Scheme = "itemref://";

		private const string Quad = "[0-9a-f]{1,8}:[0-9a-f]{1,8}:[0-9a-f]{1,8}:[0-9a-f]{1,8}";

		private static readonly Regex LinkRx = new Regex(
			"^<a style=\"text-decoration:none\" href=\"itemref://(?<t1>\\d+)/(?<t2>\\d+)/(?<t3>\\d+)/(?<level>\\d+)/" +
			$"(?<k1>{Quad})/(?<k2>{Quad})/(?<k3>{Quad})\"><font color=#(?<colour>[0-9a-f]+)>\\[(?<name>.*)\\]</font></a>$",
			RegexOptions.Compiled);

		public static string Build(ItemRefLink value)
		{
			ArgumentNullException.ThrowIfNull(value);
			return $"<a style=\"text-decoration:none\" href=\"itemref://{value.TemplateId}/{value.TemplateId2}/{value.TemplateId3}/{value.Level}/" +
			       $"{Hex(value.Key1)}/{Hex(value.Key2)}/{Hex(value.Key3)}\"><font color=#{value.Colour}>[{value.Name}]</font></a>";
		}

		// THROWS on anything that is not the anchor above (rule 4). A chat link is PLAYER-TYPED TEXT reaching an
		// importer, so a shape we cannot rebuild is rejected outright rather than half-read - there is no blob column
		// to fall back on, and half-reading one would put an invented key on an item row.
		public static ItemRefLink Parse(string text)
		{
			if (string.IsNullOrEmpty(text))
				throw new CodecParseException("ItemRefLinkCodec: the link is empty.");

			var m = LinkRx.Match(text);
			if (!m.Success)
				throw new CodecParseException($"ItemRefLinkCodec: not a recognised itemref anchor: {text}");

			return new ItemRefLink(Num(m, "t1"), Num(m, "t2"), Num(m, "t3"), Num(m, "level"), Key(m, "k1"), Key(m, "k2"), Key(m, "k3"),
				m.Groups["colour"].Value, m.Groups["name"].Value);
		}

		private static uint Num(Match m, string group)
		{
			return uint.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
		}

		private static ItemRefKey Key(Match m, string group)
		{
			var parts = m.Groups[group].Value.Split(':').Select(p => uint.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
			return new ItemRefKey(parts[0], parts[1], parts[2], parts[3]);
		}

		private static string Hex(ItemRefKey key)
		{
			return $"{key.Key1:x}:{key.Key2:x}:{key.Key3:x}:{key.Key4:x}";
		}
	}

	// One item chat link. `TemplateId2` is the level-scaled pair's high endpoint (#752) and equals `TemplateId` on
	// everything that is not a pair; `TemplateId3` is 0 on every sighting in the corpus. `Colour` is the quality
	// colour the client rendered the name in, carried as the raw hex text so the link rebuilds byte-for-byte.
	public sealed record ItemRefLink(uint TemplateId, uint TemplateId2, uint TemplateId3, uint Level, ItemRefKey Key1, ItemRefKey Key2, ItemRefKey Key3,
		string Colour, string Name)
	{
		// The `NullIdentity` sentinel a pre-existence item carries in `key3` - not a key, and never to be stored as
		// one. Two of the corpus's sightings carry it.
		public static readonly ItemRefKey NullIdentity = new ItemRefKey(0xB290805C, 0x29E627CA, 0xB290805C, 0x29E627CA);

		// Whether this link states a REAL second template - the high endpoint of a level-scaled item, with its own
		// key. `TemplateId2 == TemplateId` is the ordinary case and says nothing (see ItemHashHarvester.CapturedPair).
		public bool HasPair => TemplateId2 != 0 && TemplateId2 != TemplateId;
	}

	// One four-word TEA key quad as the link spells it.
	public readonly record struct ItemRefKey(uint Key1, uint Key2, uint Key3, uint Key4);
}
