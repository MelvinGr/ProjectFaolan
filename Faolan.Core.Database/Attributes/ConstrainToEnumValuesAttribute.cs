using System;
using System.Linq;
using System.Reflection;
using Faolan.Core.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Faolan.Core.Database.Attributes
{
	// Marks an enum-typed entity property whose column must be constrained to the defined values of its enum.
	// DatabaseContext.OnModelCreating scans for this and emits a generated check constraint - "<Column> in (<enum
	// values>)" - so the allowed set stays in sync with the enum automatically (add/remove an enum member and the
	// constraint follows on the next migration). Put it on closed enums only: Not open ones like CharacterStatId,
	// which carries Unknown fillers for still-undecoded ids.
	//
	// It is also meaningless on a [Flags] enum, and dangerously so: "IN (<declared members>)" constrains the column
	// to exactly one member, which is the opposite of what flags mean - a real combination like
	// `GmPrivileges.GodMode | GmPrivileges.CanCloak` (0x00100200) is not a declared member and the check rejects it.
	// Nothing would catch that at build or migration time; it would surface as a constraint violation from the database
	// on the first SaveChanges that stored a combination. So a [Flags] property is skipped with a warning rather than
	// constrained - see ApplyEnumCheckConstraints.
	[AttributeUsage(AttributeTargets.Property)]
	public sealed class ConstrainToEnumValuesAttribute : Attribute
	{
		// Emits a generated check constraint for every property marked [ConstrainToEnumValues]: "<Column> in (<the
		// enum's defined integer values>)". Building the list from the enum keeps the DB constraint in sync with the
		// enum automatically, so a single attribute on the table property is all a new constrained column needs.
		// `quoteColumns` delimits the column identifier in the check SQL. SQLite folds an unquoted identifier
		// case-insensitively so the bare `State IN (...)` form has always worked there; PostgreSQL folds an unquoted
		// identifier to lower-case (`state`), which does not exist, so on Npgsql the name must be double-quoted
		// (`"State"`). Kept provider-conditional so the SQLite check text - baked into the squashed migration and
		// every faolan.db built from it - stays byte-for-byte unchanged.
		public static void ApplyEnumCheckConstraints(ModelBuilder modelBuilder, bool quoteColumns = false)
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

					// A [Flags] enum cannot be expressed as "IN (<members>)" - see the type remarks. Skip it rather
					// than emit a constraint that rejects legitimate combinations, and say so: silently doing nothing
					// would leave the author believing the column is constrained when it is not.
					if (enumType.IsDefined(typeof(FlagsAttribute), false))
					{
						Console.Error.WriteLine($"[ConstrainToEnumValues] SKIPPED {entityType.ClrType.Name}.{property.Name}: {enumType.Name} is a [Flags] enum, " +
						                        "so an IN (<declared members>) constraint would reject valid combinations. The column is NOT constrained.");
						continue;
					}

					var values = Enum.GetValues(enumType).Cast<object>().Select(Convert.ToInt64).Distinct().OrderBy(v => v).JoinAsString(", ");
					var column = property.GetColumnName(store.Value);
					var columnRef = quoteColumns
						? $"\"{column}\""
						: column;
					entityType.AddCheckConstraint($"CK_{entityType.ClrType.Name}_{property.Name}", $"{columnRef} IN ({values})");
				}
			}
		}
	}
}
