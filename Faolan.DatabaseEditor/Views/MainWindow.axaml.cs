using Avalonia.Controls;
using Faolan.DatabaseEditor.ViewModels;

namespace Faolan.DatabaseEditor.Views
{
	public partial class MainWindow : Window
	{
		private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

		public MainWindow(MainWindowViewModel viewModel)
		{
			InitializeComponent();

			DataContext = viewModel;
		}
	}
}
