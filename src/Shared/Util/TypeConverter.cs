using System;
using System.Collections.Generic;
using System.Text;

namespace WKMPMod.Util;

public static class TypeConverter {
	public static bool ToBool(string value) {
		if (string.IsNullOrWhiteSpace(value))
			return false;

		value = value.Trim().ToLowerInvariant();

		// 支持 true, false, 1, 0
		return value switch {
			"true" or "1" => true,
			"false" or "0" => false,
			_ => throw new FormatException("Cannot convert '" + value + "' to a boolean value.")
		};
	}
}

