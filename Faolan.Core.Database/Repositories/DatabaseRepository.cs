using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	/// <summary>
	///     The aggregate database port: composes the per-domain repository interfaces, each declared next to its
	///     implementation in DatabaseRepository.&lt;Domain&gt;.cs. This interface itself carries only the cross-cutting
	///     unit-of-work seam.
	/// </summary>
	/// <remarks>
	///     A consumer takes the narrow slices it actually calls; only one that genuinely needs most of the surface, or
	///     that hands the repository on to a helper needing the composite, takes <see cref="IDatabaseRepository" />.
	/// </remarks>
	public interface IDatabaseRepository : IAccountRepository, ICharacterRepository, ICharacterInventoryRepository, ICharacterQuestRepository, ICharacterStatRepository, ICharacterProgressionRepository, ICatalogRepository, IEconomyRepository, IFactionRepository, IGuildRepository, INpcRepository, IRealmRepository, IWorldRepository
	{
		Task Execute(Func<IDatabaseContext, Task> work);
		Task<T> Query<T>(Func<IDatabaseContext, Task<T>> work);
	}

	public partial class DatabaseRepository : IDatabaseRepository
	{
		private readonly IDbContextFactory<DatabaseContext> _contextFactory;

		public DatabaseRepository(IDbContextFactory<DatabaseContext> contextFactory)
		{
			_contextFactory = contextFactory;
		}

		public async Task Execute(Func<IDatabaseContext, Task> work)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			await work(ctx);
		}

		public async Task<T> Query<T>(Func<IDatabaseContext, Task<T>> work)
		{
			await using var ctx = await _contextFactory.CreateDbContextAsync();
			return await work(ctx);
		}
	}
}
