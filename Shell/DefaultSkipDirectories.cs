using System;
using System.Collections.Generic;

namespace nb.Shell
{
    public static class DefaultSkipDirectories
    {
        public static readonly HashSet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            "node_modules",
            "bin",
            "obj",
            ".vs",
            "__pycache__",
            ".venv",
            "venv",
            ".idea",
            "dist",
            "build",
            ".next",
            ".nuget"
        };
    }
}