using System;
using System.Linq;

namespace CrusaderWars.unit_mapper;

public static class MapperXPath
{
    /// <summary>An XPath1.0 literal, including values containing both quote characters.</summary>
    public static string Literal(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\'')) return "'" + value + "'";
        if (!value.Contains('"')) return "\"" + value + "\"";
        return "concat(" + string.Join(", \"'\", ", value.Split('\'').Select(part => "'" + part + "'")) + ")";
    }
}
