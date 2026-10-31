using System.Collections.Generic;

namespace Faolan.Core.Extensions
{
	// ReSharper disable once InconsistentNaming
	public static class IEnumerableExtensions
	{
		extension<T>(IEnumerable<T> items)
		{
			public string JoinAsString(string separator)
			{
				return string.Join(separator, items);
			}
		}
	}
}
