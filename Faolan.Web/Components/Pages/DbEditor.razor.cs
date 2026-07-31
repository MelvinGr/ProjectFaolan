using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	public partial class DbEditor
	{
		private const int FkOptionCap = 1000; // bigger FK targets (Items/Spells) keep the plain number box

		private const int RowCap = 500;
		private static readonly MethodInfo SetMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), 1, Type.EmptyTypes)!;
		private readonly Dictionary<string, FkOptions> _fks = [];
		private readonly List<object> _rows = [];
		private IReadOnlyList<DbEditorField> _fields = [];
		private List<Type> _tables = [];
		private Type? _table;
		private object? _row;

		protected override async Task LoadAsync(DatabaseContext ctx)
		{
			_tables = ctx.Model.GetEntityTypes().Where(t => t.ClrType != null && !t.IsOwned()).Select(t => t.ClrType).Distinct().OrderBy(t => t.Name).ToList();
			if (_tables.Count > 0)
				SelectTable(_tables[0]);
		}

		private void AddRow()
		{
			if (Ctx == null || _table == null)
				return;

			try
			{
				var entity = Activator.CreateInstance(_table)!;
				Ctx.Add(entity);
				_rows.Insert(0, entity);
				SelectRow(entity);
				Set($"Added a new {_table.Name} (unsaved — press Save)");
			}
			catch (Exception ex)
			{
				Set(ex.Message, true);
			}
		}

		private void ComputeForeignKeys(Type t)
		{
			var et = Ctx!.Model.FindEntityType(t);
			if (et == null)
				return;

			foreach (var fk in et.GetForeignKeys())
			{
				if (fk.Properties.Count != 1 || fk.PrincipalKey.Properties.Count != 1)
					continue;

				var column = fk.Properties[0].Name; // e.g. RealmId
				var principal = fk.PrincipalEntityType.ClrType; // e.g. Realm
				var keyProp = principal.GetProperty(fk.PrincipalKey.Properties[0].Name);
				if (keyProp == null || _fks.ContainsKey(column))
					continue;

				var items = new List<FkOption>();
				var overCap = false;
				foreach (var row in (IEnumerable)SetMethod.MakeGenericMethod(principal).Invoke(Ctx, null)!)
				{
					if (items.Count >= FkOptionCap)
					{
						overCap = true;
						break;
					}

					items.Add(new FkOption(keyProp.GetValue(row)?.ToString() ?? "", row.ToString() ?? ""));
				}

				if (!overCap)
					_fks[column] = new FkOptions(items.OrderBy(o => o.Label).ToList(), fk.Properties[0].IsNullable);
			}
		}

		private void DeleteRow()
		{
			if (Ctx == null || _row == null)
				return;

			Ctx.Remove(_row);
			_rows.Remove(_row);
			_row = null;
			_fields = [];
			Set("Removed (unsaved — press Save)");
		}

		private IEnumerable<object> Filtered()
		{
			return string.IsNullOrWhiteSpace(Filter)
				? _rows
				: _rows.Where(r => (r.ToString() ?? "").Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase));
		}

		private void SelectRow(object r)
		{
			_row = r;
			_fields = DbEditorField.Editable(r);
		}

		private void SelectTable(Type t)
		{
			_table = t;
			_row = null;
			_fields = [];
			Filter = "";
			_rows.Clear();
			_fks.Clear();
			if (Ctx == null)
				return;

			try
			{
				var set = (IEnumerable)SetMethod.MakeGenericMethod(t).Invoke(Ctx, null)!;
				foreach (var e in set)
				{
					_rows.Add(e);
					if (_rows.Count >= RowCap)
						break;
				}

				ComputeForeignKeys(t);
				Set($"{t.Name}: {_rows.Count} rows loaded");
			}
			catch (Exception ex)
			{
				Set(ex.Message, true);
			}
		}

		private void SetText(DbEditorField f, string? value)
		{
			try
			{
				f.Text = value ?? "";
			}
			catch (Exception ex)
			{
				Set($"{f.Name}: {ex.Message}", true);
			}
		}

		private sealed record FkOption(string Value, string Label);

		private sealed record FkOptions(List<FkOption> Items, bool Nullable);
	}
}
