using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.EntityFrameworkCore;

namespace Faolan.DatabaseEditor.ViewModels
{
	public sealed class MainWindowViewModel : INotifyPropertyChanged
	{
		// The editor is a single-user desktop tool: one editing session = one unit of work, so it holds a single
		// context (from the shared pooled factory) for the window's lifetime - load into it, edit tracked entities
		// via the UI, save on command. (The server, by contrast, uses short-lived per-operation contexts.)
		private readonly DatabaseContext? _context;

		// All rows loaded for the selected table, unfiltered. Rows is the filtered view bound to the UI.
		private readonly List<RowViewModel> _allRows = [];

		public AsyncRelayCommand SaveCommand { get; }

		public RelayCommand AddCommand { get; }

		public RelayCommand DeleteCommand { get; }

		// Every mapped entity type in the model - the editor follows the schema, so new tables/fields show up
		// automatically with no per-table code (the whole point: "support all the new stuff that's been added").
		public ObservableCollection<TableViewModel> Tables { get; } = [];

		public ObservableCollection<RowViewModel> Rows { get; } = [];

		public ObservableCollection<PropertyEditorViewModel> SelectedRowEditors { get; } = [];

		public TableViewModel? SelectedTable
		{
			get;
			set
			{
				if (ReferenceEquals(field, value))
					return;

				field = value;

				RefreshRows();
				OnPropertyChanged(nameof(SelectedTable));
			}
		}

		public RowViewModel? SelectedRow
		{
			get;
			set
			{
				if (ReferenceEquals(field, value))
					return;

				field = value;

				RefreshEditors();
				OnPropertyChanged(nameof(SelectedRow));

				AddCommand.RaiseCanExecuteChanged();
				DeleteCommand.RaiseCanExecuteChanged();
			}
		}

		public string Filter
		{
			get;
			set
			{
				if (field == value)
					return;

				field = value;

				ApplyFilter();
				OnPropertyChanged(nameof(Filter));
			}
		} = string.Empty;

		public string Status
		{
			get;
			private set
			{
				if (field == value)
					return;

				field = value;

				OnPropertyChanged(nameof(Status));
			}
		} = string.Empty;

		// Design-time constructor
		public MainWindowViewModel()
		{
			SaveCommand = new AsyncRelayCommand(Save);
			AddCommand = new RelayCommand(AddRow, () => SelectedTable is not null);
			DeleteCommand = new RelayCommand(DeleteRow, () => SelectedRow is not null);
		}

		// Runtime constructor
		public MainWindowViewModel(IDbContextFactory<DatabaseContext> contextFactory)
			: this()
		{
			_context = contextFactory.CreateDbContext();

			var tables = _context
				.Model
				.GetEntityTypes()
				.Where(t => t.ClrType is not null && !t.IsOwned())
				.Select(t => t.ClrType)
				.Distinct()
				.OrderBy(t => t.Name)
				.Select(t => new TableViewModel(t));

			foreach (var table in tables)
				Tables.Add(table);

			SelectedTable = Tables.FirstOrDefault();
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private void RefreshRows()
		{
			_allRows.Clear();
			SelectedRow = null;

			if (_context is not null && SelectedTable is not null)
			{
				var set = (System.Collections.IEnumerable)SetMethod
					.MakeGenericMethod(SelectedTable.ClrType)
					.Invoke(_context, null)!;

				foreach (var entity in set)
					_allRows.Add(new RowViewModel(entity));
			}

			ApplyFilter();
		}

		private void ApplyFilter()
		{
			Rows.Clear();

			var filter = Filter?.Trim();

			var matches = string.IsNullOrEmpty(filter)
				? _allRows
				: _allRows.Where(r => r.Display.Contains(filter, StringComparison.OrdinalIgnoreCase));

			foreach (var row in matches)
				Rows.Add(row);

			Status = SelectedTable is null
				? string.Empty
				: $"{SelectedTable.Name}: {Rows.Count} / {_allRows.Count} rows";

			if (!Rows.Contains(SelectedRow!))
				SelectedRow = Rows.FirstOrDefault();
		}

		private void RefreshEditors()
		{
			SelectedRowEditors.Clear();

			if (SelectedRow is null)
				return;

			AddEditors(SelectedRowEditors, SelectedRow.Entity);
		}

		private void AddRow()
		{
			if (_context is null || SelectedTable is null)
				return;

			var entity = Activator.CreateInstance(SelectedTable.ClrType);

			if (entity is null)
				return;

			_context.Add(entity);

			var row = new RowViewModel(entity);

			_allRows.Add(row);
			Rows.Add(row);
			SelectedRow = row;

			Status = $"Added a new {SelectedTable.Name} row (unsaved - press Save to persist)";
		}

		private void DeleteRow()
		{
			if (_context is null || SelectedRow is null)
				return;

			_context.Remove(SelectedRow.Entity);

			_allRows.Remove(SelectedRow);
			Rows.Remove(SelectedRow);
			SelectedRow = Rows.FirstOrDefault();

			Status = $"Removed a {SelectedTable?.Name} row (unsaved - press Save to persist)";
		}

		private async Task Save()
		{
			if (_context is null)
				return;

			var changed = await _context.SaveChangesAsync();

			Status = $"Saved - {changed} change(s) written";
		}

		private static readonly MethodInfo SetMethod =
			typeof(DbContext).GetMethod(nameof(DbContext.Set), 1, Type.EmptyTypes)!;

		private void OnPropertyChanged(string propertyName)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}

		private static void AddEditors(ObservableCollection<PropertyEditorViewModel> editors, object model)
		{
			var properties = model
				.GetType()
				.GetProperties(BindingFlags.Instance | BindingFlags.Public)
				.Where(p => p.CanRead && p.CanWrite)
				.Where(p => p.GetIndexParameters()
					.Length == 0)
				.Where(p => IsEditableType(p.PropertyType))
				.OrderBy(p => p.Name);

			foreach (var property in properties)
				editors.Add(new PropertyEditorViewModel(model, property));
		}

		private static bool IsEditableType(Type type)
		{
			type = Nullable.GetUnderlyingType(type) ?? type;

			return type == typeof(string) || type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
			       type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid) || type.IsEnum;
		}
	}
}
