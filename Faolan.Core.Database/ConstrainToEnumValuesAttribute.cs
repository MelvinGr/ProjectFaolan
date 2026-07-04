using System;
using System.Linq;
using System.Reflection;
using Faolan.Core.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Faolan.Core.Database
{
	// Marks an enum-typed entity property whose column must be constrained to the DEFINED values of its enum.
	// DatabaseContext.OnModelCreating scans for this and emits a generated CHECK constraint - "<Column> IN (<enum
	// values>)" - so the allowed set stays in sync with the enum automatically (add/remove an enum member and the
	// constraint follows on the next migration). Put it on CLOSED enums only: NOT open ones like CharacterStatId,
	// which carries Unknown fillers for still-undecoded ids.
	[AttributeUsage(AttributeTargets.Property)]
	public sealed class ConstrainToEnumValuesAttribute : Attribute
	{
		// Emits a generated CHECK constraint for every property marked [ConstrainToEnumValues]: "<Column> IN (<the
		// enum's defined integer values>)". Building the list from the enum keeps the DB constraint in sync with the
		// enum automatically, so a single attribute on the table property is all a new constrained column needs.
		public static void ApplyEnumCheckConstraints(ModelBuilder modelBuilder)
		{
			foreach (var entityType in modelBuilder.Model.GetEntityTypes())
			{
				var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table);
				if (store is null)
					continue;

				foreach (var property in entityType.GetProperties().Where(p => p.PropertyInfo?.GetCustomAttribute<ConstrainToEnumValuesAttribute>() != null))
				{
					var enumType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
					if (!enumType.IsEnum)
						continue;

					var values = Enum.GetValues(enumType).Cast<object>().Select(Convert.ToInt64).Distinct().OrderBy(v => v).JoinAsString(", ");
					var column = property.GetColumnName(store.Value);
					entityType.AddCheckConstraint($"CK_{entityType.ClrType.Name}_{property.Name}", $"{column} IN ({values})");
				}
			}
		}
	}
}
