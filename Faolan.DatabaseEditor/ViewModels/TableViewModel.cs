using System;

namespace Faolan.DatabaseEditor.ViewModels
{
	// A single mapped entity type in the model (one row in the left-hand table list).
	public sealed class TableViewModel
	{
		public Type ClrType { get; }

		public string Name => ClrType.Name;

		public TableViewModel(Type clrType)
		{
			ClrType = clrType;
		}
	}
}
