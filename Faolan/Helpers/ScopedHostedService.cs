using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Faolan.Helpers
{
	public sealed class ScopedHostedService<TService> : IHostedService, IDisposable, IAsyncDisposable where TService : class, IHostedService
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private IServiceScope? _scope;
		private TService? _service;

		public ScopedHostedService(IServiceScopeFactory scopeFactory)
		{
			_scopeFactory = scopeFactory;
		}

		public async ValueTask DisposeAsync()
		{
			if (_scope is IAsyncDisposable asyncDisposable)
				await asyncDisposable.DisposeAsync();
			else
				_scope?.Dispose();
		}

		public void Dispose()
		{
			_scope?.Dispose();
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			_scope = _scopeFactory.CreateScope();
			_service = _scope.ServiceProvider.GetRequiredService<TService>();

			return _service!.StartAsync(cancellationToken);
		}

		public async Task StopAsync(CancellationToken cancellationToken)
		{
			if (_service != null)
				await _service.StopAsync(cancellationToken);
		}
	}

	public static class HostedServiceExtensions
	{
		extension(IServiceCollection services)
		{
			public IServiceCollection AddScopedHostedService<TService>() where TService : class, IHostedService
			{
				services.AddSingleton<IHostedService, ScopedHostedService<TService>>();
				services.AddScoped<TService>();

				return services;
			}

			public IServiceCollection AddScopedHostedService<TService>(Func<IServiceProvider, TService> implementationFactory) where TService : class, IHostedService
			{
				services.AddSingleton<IHostedService, ScopedHostedService<TService>>();
				services.AddScoped(implementationFactory);

				return services;
			}
		}
	}
}
