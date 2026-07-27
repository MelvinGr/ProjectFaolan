using System;
using System.Threading.Tasks;
using Faolan.Core.Database;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	// The skeleton every admin editor page shares: the admin gate, a page-lifetime DatabaseContext, and the
	// save/status plumbing. DbEditor, QuestEditor and SpellEditor each carried their own copy.
	//
	// The copy that mattered is the GATE. Three pages independently re-implemented "resolve the cascading auth state,
	// bounce to / unless the user is in Admin" - a security check whose failure mode is silent, since a page that
	// drifted would simply stop being protected while still looking correct. Here it runs in a sealed
	// OnInitializedAsync, so a page cannot forget it or replace it: a subclass gets LoadAsync, which is only reached
	// once the gate has passed and Ctx exists.
	public abstract class AdminDbPage : ComponentBase, IDisposable
	{
		[Inject] protected IDbContextFactory<DatabaseContext> DbFactory { get; set; } = null!;

		[Inject] protected NavigationManager Nav { get; set; } = null!;

		[CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

		// Page-lifetime and deliberately long-lived, unlike the short per-unit-of-work contexts the server uses: an
		// editor binds its loaded entities straight to the form, so the tracker has to stay alive between load and
		// save for SaveChangesAsync to see the edits at all.
		protected DatabaseContext? Ctx { get; private set; }

		protected bool Authorized { get; private set; }
		protected bool Error { get; private set; }
		protected string Filter { get; set; } = "";
		protected string? Status { get; private set; }

		public virtual void Dispose()
		{
			Ctx?.Dispose();
			GC.SuppressFinalize(this);
		}

		protected sealed override async Task OnInitializedAsync()
		{
			var user = AuthState == null
				? null
				: (await AuthState).User;
			if (user == null || !user.IsInRole("Admin"))
			{
				Nav.NavigateTo("/", true);
				return;
			}

			Authorized = true;
			Ctx = await DbFactory.CreateDbContextAsync();
			await LoadAsync(Ctx);
		}

		// The page's own load. The context is a PARAMETER rather than read off Ctx so the guarantee is in the
		// signature: this runs only after the gate has passed and the context exists, and a subclass should not have
		// to null-check what the base just created.
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
