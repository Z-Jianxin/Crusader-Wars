using System.Runtime.CompilerServices;

// The offline replay harness exercises the production readers and mapper without
// opening the desktop application, launching games, or writing a campaign save.
[assembly: InternalsVisibleTo("StatFidelity.BattleHarness")]
