using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Faolan.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	public partial class DbEditor
	{
		private const int FkOptionCap = 1000; // bigger FK targets (Items/Spells) keep the plain number box

		private const int RowCap = 500;
		private static readonly MethodInfo SetMethod = typeof(DbContext).GetMethod(nameof(DbContext.Set), 1, Type.EmptyTypes)!;
		private readonly Dictionary<string, FkOptions> _fks = new();
		private readonly List<object> _rows = [];
		private bool _authorized;
		private bool _error;
		private DatabaseContext? _ctx;
		private IReadOnlyList<DbEditorField> _fields = [];
		private List<Type> _tables = [];
		private object? _row;
		private string _filter = "";
		private string? _status;
		private Type? _table;

		[CascadingParameter]
		private Task<AuthenticationState>? AuthState { get; set; }

		public void Dispose()
		{
			_ctx?.Dispose();
		}

		protected override async Task OnInitializedAsync()
		{
			var user = AuthState == null
				? null
				: (await AuthState).User;
			if (user == null || !user.IsInRole("Admin"))
			{
				Nav.NavigateTo("/", true);
				return;
			}

			_authorized = true;

			_ctx = await DbFactory.CreateDbContextAsync();
			_tables = _ctx.Model.GetEntityTypes().Where(t => t.ClrType != null && !t.IsOwned()).Select(t => t.ClrType).Distinct().OrderBy(t => t.Name).ToList();
			if (_tables.Count > 0)
				SelectTable(_tables[0]);
		}

		private void AddRow()
		{
			if (_ctx == null || _table == null)
				return;

			try
			{
				var entity = Activator.CreateInstance(_table)!;
				_ctx.Add(entity);
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
			var et = _ctx!.Model.FindEntityType(t);
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
				foreach (var row in (IEnumerable)SetMethod.MakeGenericMethod(principal).Invoke(_ctx, null)!)
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
			if (_ctx == null || _row == null)
				return;

			_ctx.Remove(_row);
			_rows.Remove(_row);
			_row = null;
			_fields = [];
			Set("Removed (unsaved — press Save)");
		}

		private IEnumerable<object> Filtered()
		{
			return string.IsNullOrWhiteSpace(_filter)
				? _rows
				: _rows.Where(r => (r.ToString() ?? "").Contains(_filter.Trim(), StringComparison.OrdinalIgnoreCase));
		}

		private async Task Save()
		{
			if (_ctx == null)
				return;

			try
			{
				var n = await _ctx.SaveChangesAsync();
				Set($"Saved — {n} change(s) written");
			}
			catch (Exception ex)
			{
				Set(ex.InnerException?.Message ?? ex.Message, true);
			}
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
			_filter = "";
			_rows.Clear();
			_fks.Clear();
			if (_ctx == null)
				return;

			try
			{
				var set = (IEnumerable)SetMethod.MakeGenericMethod(t).Invoke(_ctx, null)!;
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

		private void Set(string msg, bool error = false)
		{
			_status = msg;
			_error = error;
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
