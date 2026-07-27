using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	// An AdminPage that also owns a database context: the editor pages (DbEditor, QuestEditor, SpellEditor) add a
	// page-lifetime DatabaseContext and the same save/status plumbing on top of the gate.
	public abstract class AdminDbPage : AdminPage, IDisposable
	{
		[Inject] protected IDbContextFactory<DatabaseContext> DbFactory { get; set; } = null!;

		// Page-lifetime and deliberately long-lived, unlike the short per-unit-of-work contexts the server uses: an
		// editor binds its loaded entities straight to the form, so the tracker has to stay alive between load and
		// save for SaveChangesAsync to see the edits at all.
		protected DatabaseContext? Ctx { get; private set; }

		protected bool Error { get; private set; }
		protected string Filter { get; set; } = "";
		protected string? Status { get; private set; }

		public virtual void Dispose()
		{
			Ctx?.Dispose();
			GC.SuppressFinalize(this);
		}

		// Sealed: the context is created here, once, so an editor page's own load always receives a live one.
		protected sealed override async Task LoadAsync()
		{
			Ctx = await DbFactory.CreateDbContextAsync();
			await LoadAsync(Ctx);
		}

		// The page's own load. The context is a PARAMETER rather than read off Ctx so the guarantee is in the
		// signature: this runs only after the gate has passed and the context exists, so a subclass never has to
		// null-check what the base just created.
		protected abstract Task LoadAsync(DatabaseContext ctx);

		protected async Task Save()
		{
			if (Ctx == null)
				return;

			try
			{
				var n = await Ctx.SaveChangesAsync();
				Set($"Saved — {n} change(s) written. Restart the server to apply.");
			}
			catch (Exception ex)
			{
				// The CHECK constraints (ConstrainToEnumValues) and composite keys surface here, and the useful half
				// is the provider's message - "UNIQUE constraint failed" - not the EF wrapper around it.
				Set(ex.InnerException?.Message ?? ex.Message, true);
			}
		}

		protected void Set(string msg, bool error = false)
		{
			Status = msg;
			Error = error;
		}
	}
}
