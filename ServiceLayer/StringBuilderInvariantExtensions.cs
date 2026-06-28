using System.Globalization;
using System.Text;

namespace RoslynMcp.ServiceLayer;

internal static class StringBuilderInvariantExtensions
{
    public static StringBuilder AppendInvariant(this StringBuilder sb, FormattableString value)
    {
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
        return sb;
    }

    public static StringBuilder AppendLineInvariant(this StringBuilder sb, FormattableString value)
    {
        sb.Append(value.ToString(CultureInfo.InvariantCulture));
        return sb.AppendLine();
    }
}

