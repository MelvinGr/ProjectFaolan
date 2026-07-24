using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Faolan.Web.Services
{
	// One editable scalar property of an entity, bound to an input in the DB editor (port of the desktop
	// PropertyEditorViewModel). Reads/writes the live tracked entity by reflection, converting from the string/bool
	// the UI supplies. Navigation properties and collections are skipped (see Editable).
	public sealed class DbEditorField
	{
		private readonly PropertyInfo _prop;
		private readonly object _entity;

		public Array EnumValues => IsEnum
			? Enum.GetValues(Type)
			: Array.Empty<object>();

		public Type Type { get; }

		public bool BoolValue
		{
			get => _prop.GetValue(_entity) is true;
			set => _prop.SetValue(_entity, value);
		}

		public bool IsBool => Type == typeof(bool);
		public bool IsEnum => Type.IsEnum;
		public bool IsNullable { get; }

		public string Name => _prop.Name;

		// Text (and enum) values flow through here; the setter converts to the property's CLR type.
		public string Text
		{
			get => _prop.GetValue(_entity)?.ToString() ?? "";
			set => _prop.SetValue(_entity, Convert(value));
		}

		public DbEditorField(object entity, PropertyInfo prop)
		{
			_entity = entity;
			_prop = prop;
			Type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
			IsNullable = Nullable.GetUnderlyingType(prop.PropertyType) != null || !Type.IsValueType;
		}

		private object? Convert(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return IsNullable || Type == typeof(string)
					? null
					: Activator.CreateInstance(Type);
			}

			if (Type == typeof(string))
				return text;
			if (Type == typeof(Guid))
				return Guid.Parse(text);
			if (Type == typeof(DateTime))
				return DateTime.Parse(text, CultureInfo.InvariantCulture);
			if (Type == typeof(DateTimeOffset))
				return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
			if (Type.IsEnum)
				return Enum.Parse(Type, text);

			return System.Convert.ChangeType(text, Type, CultureInfo.InvariantCulture);
		}

		// The editable scalar properties of an entity: public read/write, no indexers, a scalar/enum CLR type.
		public static IReadOnlyList<DbEditorField> Editable(object entity)
		{
			return entity
				.GetType()
				.GetProperties(BindingFlags.Instance | BindingFlags.Public)
				.Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
				.Where(p => IsEditableType(p.PropertyType))
				.OrderBy(p => p.Name)
				.Select(p => new DbEditorField(entity, p))
				.ToList();
		}

		public static bool IsEditableType(Type type)
		{
			type = Nullable.GetUnderlyingType(type) ?? type;
			return type == typeof(string) || type == typeof(bool) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type.IsEnum || type == typeof(byte) ||
			       type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) ||
			       type == typeof(float) || type == typeof(double) || type == typeof(decimal);
		}
	}
}
