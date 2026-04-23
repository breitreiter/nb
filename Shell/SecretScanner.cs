using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace nb.Shell
{
    public static class SecretScanner
    {
        public record SecretMatch(string PatternName, string MatchedText, int StartIndex, int Length);

        private static readonly List<(string PatternName, Regex Regex)> Patterns = new()
        {
            ("AWS Access Key", new Regex(@"AKIA[A-Z2-7]{16}", RegexOptions.Compiled)),
            ("GCP API Key", new Regex(@"AIza[\w-]{35}", RegexOptions.Compiled)),
            ("JWT Token", new Regex(@"ey[a-zA-Z0-9]{17,}\.ey[a-zA-Z0-9/_-]{17,}\..*", RegexOptions.Compiled)),
            ("GitHub PAT", new Regex(@"ghp_[a-zA-Z0-9]{36}", RegexOptions.Compiled)),
            ("Anthropic API Key", new Regex(@"sk-ant-api03-[a-zA-Z0-9_-]{93}", RegexOptions.Compiled)),
            ("Generic Secret", new Regex(@"(?i)(api[_-]?key|secret|password|token)\s*[:=]\s*['""]?[a-zA-Z0-9_-]{16,}", RegexOptions.Compiled))
        };

        private static readonly Regex EnvRegex = new Regex(@"(^|[\s/])\.env(\s|$|:)", RegexOptions.Compiled);

        public static IReadOnlyList<SecretMatch> Scan(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return Array.Empty<SecretMatch>();
            }

            var matches = new List<SecretMatch>();
            foreach (var (patternName, regex) in Patterns)
            {
                foreach (Match match in regex.Matches(input))
                {
                    matches.Add(new SecretMatch(patternName, match.Value, match.Index, match.Length));
                }
            }

            matches.Sort((a, b) => a.StartIndex.CompareTo(b.StartIndex));
            return matches;
        }

        public static bool ContainsSensitivePath(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }

            if (input.Contains(".ssh/") ||
                input.Contains(".aws/credentials") ||
                input.Contains("secrets.yaml") ||
                EnvRegex.IsMatch(input))
            {
                return true;
            }

            return false;
        }
    }
}
