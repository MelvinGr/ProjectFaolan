using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Faolan.Core.Database;
using Faolan.Core.Database.Tables;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Tests.Support
{
	// The client's character-customization catalog (MorphTarget), read once from the seed fixture that TestDb builds
	// out of faolan.sql - so it needs no capture corpus and no committed side-fixture, and it is the SAME rows
	// the server resolves a spawn's appearance through.
	//
	// The spawn goldens need both directions: ByName is what generation resolves a character's editor refs through
	// (PlayerSpawnScaffoldRows.BuildWeightedEntries), RefsByTag is what extraction reads a captured entry table back
	// with (ExtractTo). Together they are what makes a captured spawn regenerable without storing its entry table.
	public static class MorphRegistry
	{
		public static IReadOnlyDictionary<string, MorphTarget> ByName => Loaded.Value.ByName;

		public static IReadOnlyDictionary<ushort, string> RefsByTag => Loaded.Value.RefsByTag;

		private static readonly Lazy<(IReadOnlyDictionary<string, MorphTarget> ByName, IReadOnlyDictionary<ushort, string> RefsByTag)> Loaded =
			new Lazy<(IReadOnlyDictionary<string, MorphTarget> ByName, IReadOnlyDictionary<ushort, string> RefsByTag)>(Load, LazyThreadSafetyMode.ExecutionAndPublication);

		private static (IReadOnlyDictionary<string, MorphTarget> ByName, IReadOnlyDictionary<ushort, string> RefsByTag) Load()
		{
			using var db = new DatabaseContext(TestDb.ReadOnly(TestDb.GameDatabase));
			var targets = db.MorphTargets.AsNoTracking().ToList();
			if (targets.Count == 0)
				throw new InvalidOperationException("the seed carries no MorphTargets, so no spawn appearance can be resolved - the fixture is broken, not the test.");

			return (targets.ToDictionary(t => t.Name), targets.ToDictionary(t => (ushort)t.Id, t => t.Name));
		}
	}
}
