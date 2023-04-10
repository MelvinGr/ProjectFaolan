using System.ComponentModel.DataAnnotations;

namespace Faolan.Core.Data
{
	public class Item : BaseEntity
	{
		[MaxLength(255)]
		public string Name { get; set; }

		//
	}
}
