using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Faolan.Tests
{
	// A service takes the repository slice it calls (`IGuildRepository`, `ICatalogRepository`, ...), and every slice must
	// resolve to the one registered repository: a slice left unforwarded fails only when a service first asks for it,
	// and a slice registered as its own instance would split the singleton.
	public class RepositorySlicesTests
	{
		[Fact]
		public void EverySlice_ResolvesToTheOneRepository()
		{
			var repository = new DatabaseRepository(null!);
			var services = new ServiceCollection();
			services.AddSingleton<IDatabaseRepository>(repository);
			services.AddRepositorySlices();

			using var provider = services.BuildServiceProvider();
			var slices = typeof(IDatabaseRepository).GetInterfaces();
			Assert.NotEmpty(slices);
			foreach (var slice in slices)
				Assert.Same(repository, provider.GetRequiredService(slice));
		}
	}
}
