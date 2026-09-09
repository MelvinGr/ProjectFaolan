namespace Faolan.Core.Database
{
	/// <summary>
	///     What <see cref="DatabaseInitializer.ReconcileRealmsAsync" /> did to the <c>Realms</c> table - reported back
	///     rather than logged, because the two callers log in different styles (Console in the pre-host bootstrap and
	///     the importer, ILogger once a host exists).
	/// </summary>
	/// <param name="Created">Realms the config declared that the database did not have.</param>
	/// <param name="Updated">
	///     Realms whose static columns the config changed, each as "name (id N): Field was -> is".
	///     ⚠️ Worth logging LOUDLY when it names a port: the login tier begins advertising the new one immediately,
	///     while a realm host that is already running keeps serving the old one until it is restarted.
	/// </param>
	/// <param name="Undeclared">
	///     Realms in the database that no config layer declares. They are left standing - four tables FK a realm - so
	///     this is a notice, not an action: either the row is obsolete and wants a deliberate data migration, or the
	///     config is missing a realm that still has characters on it.
	/// </param>
	public sealed record RealmReconciliation(string[] Created, string[] Updated, string[] Undeclared)
	{
		public static readonly RealmReconciliation Nothing = new RealmReconciliation([], [], []);

		/// <summary>Whether there is nothing at all to report - the ordinary result of a restart that changed nothing.</summary>
		/// <remarks>
		///     ⚠️ Ask this rather than comparing against <see cref="Nothing" />. A record's generated equality compares
		///     its members with <c>EqualityComparer&lt;T&gt;.Default</c>, and for an array that is REFERENCE equality -
		///     so `result == Nothing` is true only for the singleton itself and silently false for every equally empty
		///     result the reconciler builds.
		/// </remarks>
		public bool IsEmpty => Created.Length == 0 && Updated.Length == 0 && Undeclared.Length == 0;
	}
}
