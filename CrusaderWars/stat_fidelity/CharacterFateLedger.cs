using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;

namespace CrusaderWars.StatFidelity;

public sealed record ResolvedCharacterFate(bool IsSlain, bool IsCaptured, string TraitsLine);
public sealed record CharacterFateEntry(string OriginalTraitsLine, ResolvedCharacterFate Fate);

/// <summary>One injury/capture/death resolution per character in one battle.</summary>
public sealed class CharacterFateLedger
{
    private readonly Dictionary<string, CharacterFateEntry> entries = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, CharacterFateEntry> Entries => new ReadOnlyDictionary<string, CharacterFateEntry>(entries);
    public void Clear() => entries.Clear();

    public ResolvedCharacterFate Resolve(string characterId, string originalTraitsLine, Func<ResolvedCharacterFate> resolve)
    {
        if (entries.TryGetValue(characterId, out var previous))
        {
            if (previous.OriginalTraitsLine != originalTraitsLine)
                throw new InvalidDataException("Character fate source traits changed within the same battle: " + characterId);
            return previous.Fate;
        }
        var result = resolve();
        if (string.IsNullOrWhiteSpace(characterId) || result.IsSlain && result.IsCaptured ||
            string.IsNullOrWhiteSpace(result.TraitsLine) || !result.TraitsLine.TrimStart().StartsWith("traits={", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid resolved character fate: " + characterId);
        entries.Add(characterId, new CharacterFateEntry(originalTraitsLine, result));
        return result;
    }

    public ResolvedCharacterFate Get(string characterId) => entries.TryGetValue(characterId, out var entry)
        ? entry.Fate : throw new InvalidDataException("Character fate was not resolved before writeback: " + characterId);
}
