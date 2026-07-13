namespace Faolan.Core.Enums
{
	public enum ItemType // TODO: Maybe just use ContainerKind directly instead of this enum?
	{
		Normal = (int)ContainerKind.Bag,
		Resource = (int)ContainerKind.Resource,
		Quest = (int)ContainerKind.Quest
	}
}
