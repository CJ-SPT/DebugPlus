using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace DebugPlus.Utils
{
    internal static class Utils
    {
        public static T SingleDebug<T>(this IEnumerable<T> types, Func<T, bool> predicate) where T : MemberInfo
        {
            if (types == null)
            {
                throw new ArgumentNullException(nameof(types));
            }

            if (predicate == null)
            {
                throw new ArgumentNullException(nameof(predicate));
            }

            var matchingTypes = types.Where(predicate).ToArray();

            if (matchingTypes.Length > 1)
            {
                throw new InvalidOperationException($"More than one member matches the specified search pattern: {string.Join(", ", matchingTypes.Select(t => t.Name))}");
            }

            if (matchingTypes.Length == 0)
            {
                throw new InvalidOperationException("No members match the specified search pattern");
            }

            return matchingTypes[0];
        }
    }
}
