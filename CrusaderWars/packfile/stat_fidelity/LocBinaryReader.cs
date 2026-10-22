using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CrusaderWars.StatFidelity;

/// <summary>Lossless fallback for old RPFM TSV exports containing unquoted localization newlines.</summary>
public static class LocBinaryReader
{
    // Fixed CA LOC format, documented by RPFM's loc decoder:
    // https://github.com/Frodo45127/rpfm/blob/master/rpfm_lib/src/files/loc/mod.rs
    public static TsvTable Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, new UnicodeEncoding(false, false, true));
        if (stream.Length < 14 || reader.ReadUInt16() != 0xFEFF ||
            Encoding.ASCII.GetString(reader.ReadBytes(3)) != "LOC" || reader.ReadByte() != 0)
            throw new InvalidDataException("Invalid native localization header: " + path);
        int version = reader.ReadInt32();
        if (version != 1) throw new InvalidDataException("Unsupported native localization version: " + version);
        uint count = reader.ReadUInt32();
        if (count > (stream.Length - stream.Position) / 5)
            throw new InvalidDataException("Native localization entry count exceeds file size: " + path);
        var table = new TsvTable("Loc", "1", new[] { "key", "text", "tooltip" });
        for (uint i = 0; i < count; i++)
        {
            string key = ReadString(reader);
            string value = ReadString(reader);
            byte tooltip = reader.ReadByte();
            if (tooltip > 1) throw new InvalidDataException("Invalid native localization boolean: " + path);
            table.Rows.Add(new Dictionary<string, string>(StringComparer.Ordinal)
                { ["key"] = key, ["text"] = value, ["tooltip"] = tooltip == 1 ? "true" : "false" });
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Trailing bytes in native localization: " + path);
        return table;
    }

    private static string ReadString(BinaryReader reader)
    {
        int bytes = reader.ReadUInt16() * 2;
        byte[] data = reader.ReadBytes(bytes);
        if (data.Length != bytes) throw new EndOfStreamException("Truncated native localization string.");
        return new UnicodeEncoding(false, false, true).GetString(data);
    }
}
