using System.Runtime.CompilerServices;

// The CLI Exe and the test project both consumed engine internals when everything
// lived in one assembly; preserve that access across the split.
[assembly: InternalsVisibleTo("nb")]
[assembly: InternalsVisibleTo("nb.Tests")]
