using Microsoft.Extensions.DependencyInjection;

namespace Faolan.Mcp
{
	public static class FaolanMcpServiceCollectionExtensions
	{
		/// <summary>
		///     Registers the in-process MCP server. It is a plain (singleton) hosted service so the
		///     <see cref="FaolanMcpHostedService" /> resolves Faolan's root-container singletons directly.
		/// </summary>
		public static IServiceCollection AddFaolanMcp(this IServiceCollection services)
		{
			services.AddHostedService<FaolanMcpHostedService>();
			return services;
		}
	}
}
