using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Faolan.PacketAnalyzer.ViewModels;

namespace Faolan.PacketAnalyzer
{
	public class ViewLocator : IDataTemplate
	{
		public bool Match(object? data)
		{
			return data is ViewModelBase;
		}

		public Control? Build(object? data)
		{
			if (data is null)
				return null;

			var name = data.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
			var type = Type.GetType(name);

			if (type != null)
			{
				var control = (Control)Activator.CreateInstance(type)!;
				control.DataContext = data;
				return control;
			}

			return new TextBlock { Text = "Not Found: " + name };
		}
	}
}
