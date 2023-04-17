using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace Faolan.DatabaseEditor.ViewModels
{
	public sealed class PropertyEditorViewModel : INotifyPropertyChanged
	{
		private readonly object _model;
		private readonly PropertyInfo _property;

		public Array? EnumValues { get; }

		public bool BooleanValue
		{
			get => Value is true;
			set => Value = value;
		}

		public bool IsBoolean { get; }

		public bool IsDateTime { get; }

		public bool IsEnum { get; }

		public bool IsGuid { get; }

		public bool IsNumber { get; }

		public bool IsString { get; }

		public bool UsesTextBox => IsString || IsNumber || IsDateTime || IsGuid;

		public object? Value
		{
			get => _property.GetValue(_model);
			set
			{
				var convertedValue = ConvertValue(value);

				_property.SetValue(_model, convertedValue);

				OnPropertyChanged(nameof(Value));
				OnPropertyChanged(nameof(TextValue));
				OnPropertyChanged(nameof(BooleanValue));
			}
		}

		public string Name => _property.Name;

		public string? TextValue
		{
			get => Value?.ToString();
			set => Value = value;
		}

		public Type PropertyType { get; }

		public PropertyEditorViewModel(object model, PropertyInfo property)
		{
			_model = model;
			_property = property;

			PropertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

			IsString = PropertyType == typeof(string);
			IsBoolean = PropertyType == typeof(bool);
			IsEnum = PropertyType.IsEnum;
			IsDateTime = PropertyType == typeof(DateTime);
			IsGuid = PropertyType == typeof(Guid);

			IsNumber = PropertyType == typeof(byte) || PropertyType == typeof(sbyte) || PropertyType == typeof(short) || PropertyType == typeof(ushort) || PropertyType == typeof(int) || PropertyType == typeof(uint) ||
			           PropertyType == typeof(long) || PropertyType == typeof(ulong) || PropertyType == typeof(float) || PropertyType == typeof(double) || PropertyType == typeof(decimal);

			EnumValues = IsEnum
				? Enum.GetValues(PropertyType)
				: null;
		}

		public event PropertyChangedEventHandler? PropertyChanged;

		private object? ConvertValue(object? value)
		{
			var targetType = Nullable.GetUnderlyingType(_property.PropertyType) ?? _property.PropertyType;
			var isNullable = Nullable.GetUnderlyingType(_property.PropertyType) is not null;

			if (value is null)
			{
				return isNullable || !targetType.IsValueType
					? null
					: Activator.CreateInstance(targetType);
			}

			if (value is string text)
			{
				if (string.IsNullOrWhiteSpace(text))
				{
					return isNullable || targetType == typeof(string)
						? null
						: Activator.CreateInstance(targetType);
				}

				if (targetType == typeof(string))
					return text;

				if (targetType == typeof(Guid))
					return Guid.Parse(text);

				if (targetType == typeof(DateTime))
					return DateTime.Parse(text, CultureInfo.CurrentCulture);

				if (targetType.IsEnum)
					return Enum.Parse(targetType, text);

				return Convert.ChangeType(text, targetType, CultureInfo.CurrentCulture);
			}

			if (targetType.IsEnum && value.GetType() != targetType)
				return Enum.ToObject(targetType, value);

			return Convert.ChangeType(value, targetType, CultureInfo.CurrentCulture);
		}

		private void OnPropertyChanged(string propertyName)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}
}
