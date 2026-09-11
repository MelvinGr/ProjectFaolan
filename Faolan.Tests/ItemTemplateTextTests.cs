using System;
using System.Collections.Generic;
using System.Text;
using Faolan.Importer.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// The item template's own text - its display name and tooltip description.
	//
	// The templates themselves are client data and are not in this repo, so the fixtures here are built to the two
	// layouts rather than lifted: what the gate is about is that one reader handles both framings and refuses
	// anything that does not frame exactly. The shapes come from real records -
	//   1000020 `Animated Dagger` : tag, lengths 0x0F and 0x20, then the two strings back to back;
	//   1000620 `Self-Resurrection Potion` : no tag, 24-byte name, 337-byte description ending the record.
	public class ItemTemplateTextTests
	{
		private const uint StringTag = 0x21;

		// `[u32 type][u32 id][u32 1000000]` then `[u32 0x21][u16 nameLen][u16 descLen][name][description]`.
		private static byte[] Headered(string name, string description)
		{
			var bytes = new List<byte>();
			bytes.AddRange(BitConverter.GetBytes(1000020u));
			bytes.AddRange(BitConverter.GetBytes(8757u));
			bytes.AddRange(BitConverter.GetBytes(1000000u));
			bytes.AddRange(BitConverter.GetBytes(0x1F4u)); // a property pair ahead of the strings, as the real records have
			bytes.AddRange(BitConverter.GetBytes(0x15u));
			bytes.AddRange(BitConverter.GetBytes(StringTag));
			bytes.AddRange(BitConverter.GetBytes((ushort)name.Length));
			bytes.AddRange(BitConverter.GetBytes((ushort)description.Length));
			bytes.AddRange(Encoding.ASCII.GetBytes(name));
			bytes.AddRange(Encoding.ASCII.GetBytes(description));
			bytes.AddRange(BitConverter.GetBytes(2u)); // the record continues past the strings

			return [.. bytes];
		}

		// No header and no tag: `[u16 nameLen][name][u16 descLen][description]`, ending the record.
		private static byte[] Unheadered(string name, string description)
		{
			var bytes = new List<byte>();
			bytes.AddRange(BitConverter.GetBytes(5u));
			bytes.AddRange(BitConverter.GetBytes(30029u));
			bytes.AddRange(BitConverter.GetBytes(1u));
			bytes.AddRange(BitConverter.GetBytes((ushort)name.Length));
			bytes.AddRange(Encoding.ASCII.GetBytes(name));
			if (description.Length > 0)
			{
				bytes.AddRange(BitConverter.GetBytes((ushort)description.Length));
				bytes.AddRange(Encoding.ASCII.GetBytes(description));
			}

			return [.. bytes];
		}

		[Fact]
		public void TaggedTemplate_YieldsBothStrings()
		{
			var (name, description) = RdbItemImporter.ExtractText(Headered("Animated Dagger", "Deals piercing damage over time."));

			Assert.Equal("Animated Dagger", name);
			Assert.Equal("Deals piercing damage over time.", description);
		}

		// The description length is a real field: zero means the item has none, which is the common case (a plain
		// armour piece carries its name and stops). It must read as "no description", never as a mis-framed read.
		[Fact]
		public void TaggedTemplate_WithNoDescription_YieldsOnlyTheName()
		{
			var (name, description) = RdbItemImporter.ExtractText(Headered("Dusklight Tunic", ""));

			Assert.Equal("Dusklight Tunic", name);
			Assert.Null(description);
		}

		// The 1000620 records - 271 of them against 98,048 - carry no RDB-Id header, so the whole blob is the body.
		// Reading them as headered is what made every one of them decode as a header mismatch and lose its row.
		[Fact]
		public void UnheaderedTemplate_YieldsBothStrings()
		{
			var potion = Unheadered("Self-Resurrection Potion", "This potion provides the imbiber with an option to resurrect themselves at the location of their death.");

			var (name, description) = RdbItemImporter.ExtractText(potion, headered: false);

			Assert.Equal("Self-Resurrection Potion", name);
			Assert.StartsWith("This potion provides the imbiber", description);
		}

		[Fact]
		public void UnheaderedTemplate_WithNoDescription_YieldsOnlyTheName()
		{
			var (name, description) = RdbItemImporter.ExtractText(Unheadered("Combatant's Hauberk", ""), headered: false);

			Assert.Equal("Combatant's Hauberk", name);
			Assert.Null(description);
		}

		// The untagged form has nothing to anchor on but its own framing, so it only accepts a chain that lands
		// exactly on the last byte. A property value that happens to look like a length must not become a name.
		[Fact]
		public void UnheaderedTemplate_RejectsAChainThatDoesNotEndTheRecord()
		{
			var trailing = new List<byte>(Unheadered("Red Apple", "An autumn fruit, fresh and crisp."));
			trailing.AddRange(BitConverter.GetBytes(0x0BADu));

			var (name, _) = RdbItemImporter.ExtractText([.. trailing], headered: false);

			Assert.NotEqual("Red Apple", name);
		}

		// Asset paths share the record and are not text: no captured item name or description carries an underscore.
		[Fact]
		public void AssetPaths_AreNotText()
		{
			var (name, description) = RdbItemImporter.ExtractText(Headered("armor_aquilonian_body", "*bodymesh_204"));

			Assert.Null(name);
			Assert.Null(description);
		}

		[Fact]
		public void AnEmptyTemplate_YieldsNothing()
		{
			var (name, description) = RdbItemImporter.ExtractText([]);

			Assert.Null(name);
			Assert.Null(description);
		}
	}
}
