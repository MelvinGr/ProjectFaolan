using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	// The aggregate database port: composes the cohesive per-domain repository interfaces (each defined next to its
	// implementation in DatabaseRepository.<Domain>.cs) so a consumer that genuinely needs everything can keep taking
	// IDatabaseRepository unchanged, while a consumer that only touches one domain can depend on the narrow interface
	// (interface segregation - Gitea #353). IDatabaseRepository itself carries only the cross-cutting unit-of-work seam.
	public interface IDatabaseRepository : IAccountRepository, ICharacterRepository, ICatalogRepository, IEconomyRepository, IFactionRepository, IGuildRepository, INpcRepository, IRealmRepository, IWorldRepository
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
