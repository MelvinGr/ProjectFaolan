using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Repositories
{
	public partial interface IDatabaseRepository
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
