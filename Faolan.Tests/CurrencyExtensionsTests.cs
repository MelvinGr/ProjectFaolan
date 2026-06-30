using Faolan.Core;
using Xunit;

namespace Faolan.Tests
{
	public class CurrencyExtensionsTests
	{
		[Theory]
		[InlineData(0ul, "0t")]
		[InlineData(5ul, "5t")]
		[InlineData(100ul, "1c")]
		[InlineData(10_000ul, "1s")]
		[InlineData(1_000_000ul, "1g")]
		[InlineData(1_010_101ul, "1g 1s 1c 1t")]
		[InlineData(1_000_001ul, "1g 1t")] // zero denominations are omitted
		[InlineData(250ul, "2c 50t")]
		public void ToCurrencyString_FormatsTinAsGSCT(ulong tin, string expected)
		{
			Assert.Equal(expected, tin.ToCurrencyString());
		}

		[Fact]
		public void ToCurrencyString_WorksForUnsignedIntWallets()
		{
			Assert.Equal("1g 1t", 1_000_001u.ToCurrencyString());
		}
	}
}
