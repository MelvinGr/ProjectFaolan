using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Faolan.Extensions
{
    /// <summary>
    ///     Extensions to emulate a typical "Startup.cs" pattern for <see cref="IHostBuilder" />
    ///     https://stackoverflow.com/questions/41407221/startup-cs-in-a-self-hosted-net-core-console-application
    /// </summary>
    public static class HostBuilderExtensions
    {
        private const string ConfigureServicesMethodName = "ConfigureServices";

        /// <summary>
        ///     Specify the startup type to be used by the host.
        /// </summary>
        /// <typeparam name="TStartup">
        ///     The type containing an optional constructor with
        ///     an <see cref="IConfiguration" /> parameter. The implementation should contain a public
        ///     method named ConfigureServices with <see cref="IHostBuilder" /> parameter.
        /// </typeparam>
        /// <param name="hostBuilder">The <see cref="IConfiguration" /> to initialize with TStartup.</param>
        /// <returns>The same instance of the <see cref="IServiceCollection" /> for chaining.</returns>
        public static IHostBuilder UseStartup<TStartup>(this IHostBuilder hostBuilder) where TStartup : class
        {
            // Invoke the ConfigureServices method on IHostBuilder...
            hostBuilder.ConfigureServices((ctx, serviceCollection) =>
            {
                // Find a method that has this signature: ConfigureServices(IServiceCollection)
                var cfgServicesMethod =
                    typeof(TStartup).GetMethod(ConfigureServicesMethodName, new[] {typeof(IServiceCollection)});

                // Check if TStartup has a ctor that takes a IConfiguration parameter
                var hasConfigCtor = typeof(TStartup).GetConstructor(new[] {typeof(IConfiguration)}) != null;

                // create a TStartup instance based on ctor
                var startUpObj = hasConfigCtor
                    ? (TStartup) Activator.CreateInstance(typeof(TStartup), ctx.Configuration)
                    : (TStartup) Activator.CreateInstance(typeof(TStartup), null);

                // finally, call the ConfigureServices implemented by the TStartup object
                cfgServicesMethod?.Invoke(startUpObj, new object[] {serviceCollection});
            });

            // chain the response
            return hostBuilder;
        }
    }
}