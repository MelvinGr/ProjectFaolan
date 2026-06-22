namespace Faolan.DatabaseEditor.ViewModels
{
	// A single loaded entity (one row in the middle list). The entity is the live EF-tracked instance, so the
	// reflection-based property editors mutate it directly and Save persists it. Display comes from the entity's
	// ToString (BaseEntity gives "Id [Type]: name"), which is plenty to pick a row by.
	public sealed class RowViewModel
	{
		public object Entity { get; }

		public string Display => Entity.ToString() ?? string.Empty;

		public RowViewModel(object entity)
		{
			Entity = entity;
		}
	}
}
