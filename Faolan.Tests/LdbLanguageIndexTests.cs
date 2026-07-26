using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Faolan.Core.Rdb;
using Xunit;

namespace Faolan.Tests
{
	// The LDB language records (RDB 1030001 "TDL1") - the client's own statement of which localized page belongs to
	// which language. These pin the parse against synthetic records shaped like the shipped ones, including the two
	// properties that made the earlier text-sniffing approach fail in-client (#367):
	//   - a language's page table is NOT word-aligned in every record (the leading name strings differ in length);
	//   - an untranslated page still contains English text, so language can only come from the declaration.
	public class LdbLanguageIndexTests
	{
		// A record shaped like the real thing: [RDB-Id 12][TDL1][name][name][plural rule][{pageId, u32} table].
		// `pad` shifts the table off a 4-byte boundary, which is what the English record does in the shipped install.
		private static byte[] LanguageRecord(uint recordId, string name, uint[] pageIds, int pad)
		{
			var body = new List<byte>();
			body.AddRange(BitConverter.GetBytes(LdbLanguageIndex.LanguageRecordType));
			body.AddRange(BitConverter.GetBytes(recordId));
			body.AddRange(BitConverter.GetBytes(1000002u));
			body.AddRange(BitConverter.GetBytes(LdbLanguageIndex.Magic));
			body.AddRange(Encoding.ASCII.GetBytes(name));
			body.Add(0);
			body.AddRange(Encoding.ASCII.GetBytes(name));
			body.Add(0);
			body.AddRange(Encoding.ASCII.GetBytes("(N == 1) /? 1 /: -1"));
			body.Add(0);
			body.AddRange(new byte[pad]);
			foreach (var page in pageIds)
			{
				body.AddRange(BitConverter.GetBytes(page));
				body.AddRange(BitConverter.GetBytes(105u)); // the record's second word per entry
			}

			return [.. body];
		}

		// The parse: name + page list, for a record whose table IS word-aligned and one whose table is NOT.
		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(3)]
		public void ReadsTheNameAndPages_AtAnyAlignment(int pad)
		{
			var pages = new uint[] { 271969, 226592, 211656 };
			var record = LanguageRecord(211832, LdbLanguageIndex.English, pages, pad);

			var (name, found) = LdbLanguageIndex.ParseRecord(record, pages);

			Assert.Equal(LdbLanguageIndex.English, name);
			Assert.Equal(pages.OrderBy(p => p), found.OrderBy(p => p));
		}

		// The failure this replaced: a page whose TEXT is English but which a language record claims for another
		// language must be attributed to that language, not to English.
		[Fact]
		public void AnUntranslatedPage_BelongsToTheLanguageThatClaimsIt()
		{
			var english = LanguageRecord(211832, LdbLanguageIndex.English, [271969], 0);
			var polish = LanguageRecord(3873745, "Polish", [3874779], 0); // its 50211 page is untranslated English

			var byPage = new Dictionary<uint, string>();
			foreach (var record in new[] { english, polish })
			{
				var (name, pages) = LdbLanguageIndex.ParseRecord(record, new uint[] { 271969, 3874779 });
				foreach (var page in pages)
					byPage[page] = name!;
			}

			Assert.Equal(LdbLanguageIndex.English, byPage[271969]);
			Assert.Equal("Polish", byPage[3874779]); // NOT English, however English its strings read
		}

	}
}
