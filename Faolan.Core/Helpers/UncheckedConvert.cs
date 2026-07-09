using System;

namespace Faolan.Core.Helpers
{
	public static class UncheckedConvert
	{
		public static byte ToByte(IConvertible value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value switch
			{
				byte v  => v,
				sbyte v => unchecked((byte)v),

				short v and >= sbyte.MinValue and <= byte.MaxValue => unchecked((byte)v),
				ushort v and <= byte.MaxValue                      => unchecked((byte)v),

				int v and >= sbyte.MinValue and <= byte.MaxValue => unchecked((byte)v),
				uint v and <= byte.MaxValue                      => unchecked((byte)v),

				long v and >= sbyte.MinValue and <= byte.MaxValue => unchecked((byte)v),
				ulong v and <= byte.MaxValue                      => unchecked((byte)v),

				char v when v <= byte.MaxValue => unchecked((byte)v),

				bool v => v
					? (byte)1
					: (byte)0,

				Enum e => ToByte(ToEnumValue(e)),

				_ => ThrowOverflow<byte>(value)
			};
		}

		public static IConvertible ToEnumValue(Enum value)
		{
			return Type.GetTypeCode(Enum.GetUnderlyingType(value.GetType())) switch
			{
				TypeCode.Byte  => Convert.ToByte(value),
				TypeCode.SByte => Convert.ToSByte(value),

				TypeCode.UInt16 => Convert.ToUInt16(value),
				TypeCode.Int16  => Convert.ToInt16(value),

				TypeCode.UInt32 => Convert.ToUInt32(value),
				TypeCode.Int32  => Convert.ToInt32(value),

				TypeCode.UInt64 => Convert.ToUInt64(value),
				TypeCode.Int64  => Convert.ToInt64(value),

				_ => throw new ArgumentOutOfRangeException(nameof(value))
			};
		}

		public static short ToInt16(IConvertible value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value switch
			{
				short v  => v,
				ushort v => unchecked((short)v),

				byte v  => v,
				sbyte v => v,

				int v and >= short.MinValue and <= ushort.MaxValue => unchecked((short)v),
				uint v and <= ushort.MaxValue                      => unchecked((short)v),

				long v and >= short.MinValue and <= ushort.MaxValue => unchecked((short)v),
				ulong v and <= ushort.MaxValue                      => unchecked((short)v),

				char v => unchecked((short)v),

				bool v => v
					? (short)1
					: (short)0,

				Enum e => ToInt16(ToEnumValue(e)),

				_ => ThrowOverflow<short>(value)
			};
		}

		public static int ToInt32(IConvertible value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value switch
			{
				int v  => v,
				uint v => unchecked((int)v),

				byte v  => v,
				sbyte v => v,

				ushort v => v,
				short v  => v,

				long v and >= int.MinValue and <= uint.MaxValue => unchecked((int)v),
				ulong v and <= uint.MaxValue                    => unchecked((int)v),

				char v => v,

				bool v => v
					? 1
					: 0,

				Enum e => ToInt32(ToEnumValue(e)),

				_ => ThrowOverflow<int>(value)
			};
		}

		public static long ToInt64(IConvertible value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value switch
			{
				long v  => v,
				ulong v => unchecked((long)v),

				byte v  => v,
				sbyte v => v,

				ushort v => v,
				short v  => v,

				uint v => v,
				int v  => v,

				char v => v,

				bool v => v
					? 1L
					: 0L,

				Enum e => ToInt64(ToEnumValue(e)),

				_ => ThrowOverflow<long>(value)
			};
		}

		public static sbyte ToSByte(IConvertible value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value switch
			{
				sbyte v => v,
				byte v  => unchecked((sbyte)v),

				short v and >= sbyte.MinValue and <= byte.MaxValue => unchecked((sbyte)v),
				ushort v and <= byte.MaxValue                      => unchecked((sbyte)v),

				int v and >= sbyte.MinValue and <= byte.MaxValue => unchecked((sbyte)v),
				uint v and <= byte.MaxValue                      => unchecked((sbyte)v),

				long v and >= sbyte.MinValue and <= byte.MaxValue => unchecked((sbyte)v),
				ulong v and <= byte.MaxValue                      => unchecked((sbyte)v),

				char v when v <= byte.MaxValue => unchecked((sbyte)v),

				bool v => v
					? (sbyte)1
					: (sbyte)0,

				Enum e => ToSByte(ToEnumValue(e)),

				_ => ThrowOverflow<sbyte>(value)
			};
		}

		public static ushort ToUInt16(IConvertible value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value switch
			{
				ushort v => v,
				short v  => unchecked((ushort)v),

				byte v  => v,
				sbyte v => unchecked((ushort)v),

				int v and >= short.MinValue and <= ushort.MaxValue => unchecked((ushort)v),
				uint v and <= ushort.MaxValue                      => unchecked((ushort)v),

				long v and >= short.MinValue and <= ushort.MaxValue => unchecked((ushort)v),
				ulong v and <= ushort.MaxValue                      => unchecked((ushort)v),

				char v => v,

				bool v => v
					? (ushort)1
					: (ushort)0,

				Enum e => ToUInt16(ToEnumValue(e)),

				_ => ThrowOverflow<ushort>(value)
			};
		}

		public static uint ToUInt32(IConvertible value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value switch
			{
				uint v => v,
				int v  => unchecked((uint)v),

				byte v  => v,
				sbyte v => unchecked((uint)v),

				ushort v => v,
				short v  => unchecked((uint)v),

				long v and >= int.MinValue and <= uint.MaxValue => unchecked((uint)v),
				ulong v and <= uint.MaxValue                    => unchecked((uint)v),

				char v => v,

				bool v => v
					? 1u
					: 0u,

				Enum e => ToUInt32(ToEnumValue(e)),

				_ => ThrowOverflow<uint>(value)
			};
		}

		public static ulong ToUInt64(IConvertible value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value switch
			{
				ulong v => v,
				long v  => unchecked((ulong)v),

				byte v  => v,
				sbyte v => unchecked((ulong)v),

				ushort v => v,
				short v  => unchecked((ulong)v),

				uint v => v,
				int v  => unchecked((ulong)v),

				char v => v,

				bool v => v
					? 1UL
					: 0UL,

				Enum e => ToUInt64(ToEnumValue(e)),

				_ => ThrowOverflow<ulong>(value)
			};
		}

		private static T ThrowOverflow<T>(IConvertible value)
		{
			throw new OverflowException($"Value '{value}' of type '{value.GetType().FullName}' does not fit in {typeof(T).Name}.");
		}
	}
}
