using System.Security.Claims;
using System.Threading.Tasks;
using Faolan.Web.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Faolan.Web.Components.Pages
{
	// The admin gate, in one place. Every /admin page had its own copy: resolve the cascading auth state, bounce to /
	// unless the user is in the "Admin" role (stamped at sign-in from the account type), then read the caller's own
	// account id out of the NameIdentifier claim.
	//
	// A per-page copy of this check fails silently - a page that drifted, or a new page whose author copied an older
	// version, would stop being protected while still looking correct. So OnInitializedAsync is sealed here and a
	// subclass gets LoadAsync instead, which is reached only once the gate has passed: a page cannot forget the gate or
	// replace it with its own variant.
	public abstract class AdminPage : ComponentBase
	{
		[Inject] protected NavigationManager Nav { get; set; } = null!;

		[CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

		protected bool Authorized { get; private set; }

		// The signed-in admin's own account id, so a page can refuse to let them change their own privileges.
		protected uint SelfId { get; private set; }

		protected sealed override async Task OnInitializedAsync()
		{
			var user = await AdminUser();
			if (user == null)
			{
				Nav.NavigateTo("/", true);
				return;
			}

			uint.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var selfId);
			SelfId = selfId;
			Authorized = true;
			await LoadAsync();
		}

		// The gate again whenever the cascading auth state changes under an open page: AccountAuthenticationStateProvider
		// turns the circuit's user anonymous once the account is banned or its role changes, and the page must stop
		// working for them then, not at their next reload.
		protected sealed override async Task OnParametersSetAsync()
		{
			if (Authorized && await AdminUser() == null)
			{
				Authorized = false;
				Nav.NavigateTo("/", true);
			}
		}

		// The page's own load, run only after the gate has passed.
		protected abstract Task LoadAsync();

		private async Task<ClaimsPrincipal?> AdminUser()
		{
			var user = AuthState == null
				? null
				: (await AuthState).User;
			return user != null && user.IsInRole(AccountPrincipal.AdminRole)
				? user
				: null;
		}
	}
}
