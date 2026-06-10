using System;
using System.IO;
using Avalonia;
using Faolan.Core;
using Faolan.DatabaseEditor.ViewModels;
using Faolan.DatabaseEditor.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Faolan.DatabaseEditor
{
	public static class Program
	{
		public static IServiceProvider Services { get; private set; } = null!;

		[STAThread]
		public static int Main(string[] args)
		{
			var builder = Host.CreateApplicationBuilder(args);

			if (Statics.IsDebug)
				builder.Environment.EnvironmentName = "Development";

			builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
			builder.Configuration.AddJsonFile(Path.GetFullPath("../../../faolan.json"));
			builder.Configuration.AddCommandLine(args);

			builder.Services.ConfigureFaolanDatabase(builder.Configuration);

			builder.Services.AddSingleton<MainWindow>();
			builder.Services.AddTransient<MainWindowViewModel>();

			Services = builder.Build()
				.Services;

			AppBuilder
				.Configure<App>()
				.UsePlatformDetect()
				.WithInterFont()
				.LogToTrace()
				.StartWithClassicDesktopLifetime(args);

			return 0;
		}
	}
}
