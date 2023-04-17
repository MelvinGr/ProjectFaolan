using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Faolan.DatabaseEditor.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Faolan.DatabaseEditor
{
	public class App : Application
	{
		public override void Initialize()
		{
			AvaloniaXamlLoader.Load(this);
		}

		public override void OnFrameworkInitializationCompleted()
		{
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
				desktop.MainWindow = Program.Services.GetRequiredService<MainWindow>();

			base.OnFrameworkInitializationCompleted();
		}
	}
}
