using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Extensions
{
	// ReSharper disable once InconsistentNaming
	public static class IEnumerableExtensions
	{
		extension<T>(IEnumerable<T> items)
		{
			public List<T> AppendRange(IEnumerable<T> items2)
			{
				var x = items.ToList();
				x.AddRange(items2);

				return x;
			}

			public string JoinAsString(string separator)
			{
				return string.Join(separator, items);
			}
		}
	}
}
