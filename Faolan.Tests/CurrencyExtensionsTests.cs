using Faolan.Core.Extensions;
using Xunit;

namespace Faolan.Tests
{
	public class CurrencyExtensionsTests
	{
		// Denominations that come out zero are omitted from the default formatting.
		[Theory, InlineData(0ul, "0t"), InlineData(5ul, "5t"), InlineData(100ul, "1c"), InlineData(10_000ul, "1s"), InlineData(1_000_000ul, "1g"), InlineData(1_010_101ul, "1g 1s 1c 1t"),
		 InlineData(1_000_001ul, "1g 1t"), InlineData(250ul, "2c 50t")]
		public void ToCurrencyString_FormatsTinAsGSCT(ulong tin, string expected)
		{
			Assert.Equal(expected, tin.ToCurrencyString());
		}

		[Theory, InlineData(0ul, "0g 0s 0c 0t"), InlineData(250ul, "0g 0s 2c 50t"), InlineData(1_010_101ul, "1g 1s 1c 1t")]
		public void ToCurrencyString_ShowAllUnits_AlwaysEmitsAllFour(ulong tin, string expected)
		{
			Assert.Equal(expected, tin.ToCurrencyString(true));
		}

		[Fact]
		public void ToCurrencyString_WorksForUnsignedIntWallets()
		{
			Assert.Equal("1g 1t", 1_000_001u.ToCurrencyString());
		}
	}
}
