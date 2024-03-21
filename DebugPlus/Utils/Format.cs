using System;
using System.Text.RegularExpressions;

namespace DebugPlus.Utils
{
    internal static class Format
    {
        public static string FormatString(string format, object[]? args = null)
        {
            try
            {
                if (args != null) 
                {
                    // First replace all { and } with {{ and }}
                    format = format.Replace("{", "{{");
                    format = format.Replace("}", "}}");

                    // Find any instance of "{{\d}}" and unescape its brackets
                    format = Regex.Replace(format, @"{{(\d+)}}", "{$1}"); ;

                    format = string.Format(format, args);
                }
            }
            catch (Exception)
            {
                Plugin.Log.LogError($"Error formatting string: {format}");

                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        Plugin.Log.LogError($"  args[{i}] = {args[i]}");
                    }
                }
                return string.Empty;
            }

            return RemoveColorBrackets(format);
        }

        /// <summary>
        /// find and replace any instance of color tags
        /// </summary>
        /// <param name="format"></param>
        /// <returns></returns>
        public static string RemoveColorBrackets(string format)
        {
            return Regex.Replace(format, @"<color=(.*?)>(.*?)</color>", "$2");
        }
    }
}
