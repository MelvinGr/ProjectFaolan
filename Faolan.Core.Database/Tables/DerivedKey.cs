using System;
using System.Globalization;
using System.Text;

namespace Faolan.Core.Database.Tables
{
	// A primary key DERIVED from a row's natural identity, for the importer-rebuilt tables whose identity is too
	// wide (or too float-y) to carry down a tree as a composite key.
	//
	// Why these tables need one at all: the importer delete+reinserts them on every re-harvest, so an AUTOINCREMENT
	// surrogate is reassigned every run - thousands of faolan.sql lines churn for zero semantic change, and it
	// cascades into every child FK. A natural composite key is the first choice (see ShopItem, NpcSpawnEquipment,
	// ...); this is the fallback for a ROOT whose identity would otherwise widen every level beneath it, or which
	// is pointed at by a NULLABLE foreign key (a composite cannot express SetNull when one of its columns is
	// non-null). The id is a pure function of the identity, so a re-import reproduces it exactly.
	//
	// FNV-1a over a NUL-separated rendering. NUL is the separator because no identity component can contain one:
	// they are entity names, integers, enum names and float bit patterns. Floats are rendered as their BIT PATTERN
	// rather than as text - the importer stores the captured bits verbatim, and a float's decimal rendering is
	// neither injective nor round-trip-stable, so the bits are the only honest identity.
	//
	// Collisions are possible in principle (32 bits). Two identities hashing alike surfaces as a PRIMARY KEY
	// violation that aborts the import - never as one row silently overwriting another. ImportConversations checks
	// for it up front as well, so its error names the two colliding screens instead of a constraint code.
	// A row whose primary key is derived from its own identity. DatabaseContext STAMPS the id of every Added
	// entity implementing this before it saves, so no caller has to remember to - and none can get it wrong,
	// because the id is by definition a function of columns the row already carries. DatabaseConventionTests
	// asserts the shipped data agrees with the function.
	public interface IDerivedKey
	{
		uint Id { get; set; }

		uint ComputeId();
	}

	public static class DerivedKey
	{
		public static uint From(params object?[] parts)
		{
			ArgumentNullException.ThrowIfNull(parts);
			var identity = new StringBuilder();
			foreach (var part in parts)
			{
				if (identity.Length > 0)
					identity.Append('\0');
				identity.Append(Render(part));
			}

			var hash = 2166136261u;
			foreach (var b in Encoding.UTF8.GetBytes(identity.ToString()))
			{
				hash ^= b;
				hash *= 16777619u;
			}

			return hash;
		}

		// A null component renders empty - it is a MEANINGFUL part of the identity ("unconditional", "unnamed"),
		// distinct from an empty string only in that no real value renders empty for the types we key on.
		private static string Render(object? part)
		{
			return part switch
			{
				null => "",
				// Before IFormattable: a float's identity is its bits, not its decimal form.
				float f => BitConverter.SingleToUInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture),
				double d => BitConverter.DoubleToUInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture),
				IFormattable v => v.ToString(null, CultureInfo.InvariantCulture),
				_ => part.ToString() ?? ""
			};
		}
	}
}
