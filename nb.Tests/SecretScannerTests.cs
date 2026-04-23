using System;
using nb.Shell;
using Xunit;

namespace nb.Tests
{
    public class SecretScannerTests
    {
        [Fact]
        public void Scan_AwsAccessKey_Detected()
        {
            var input = "AKIA" + new string('A', 16);
            var results = SecretScanner.Scan(input);
            Assert.Single(results);
            var match = results[0];
            Assert.Equal("AWS Access Key", match.PatternName);
            Assert.Equal(input, match.MatchedText);
            Assert.Equal(0, match.StartIndex);
            Assert.Equal(input.Length, match.Length);
        }

        [Fact]
        public void Scan_GcpApiKey_Detected()
        {
            var input = "AIza" + new string('A', 35);
            var results = SecretScanner.Scan(input);
            Assert.Single(results);
            var match = results[0];
            Assert.Equal("GCP API Key", match.PatternName);
            Assert.Equal(input, match.MatchedText);
        }

        [Fact]
        public void Scan_JwtToken_Detected()
        {
            var input = "ey" + new string('a', 17) + ".ey" + new string('a', 17) + ".signature";
            var results = SecretScanner.Scan(input);
            Assert.Single(results);
            var match = results[0];
            Assert.Equal("JWT Token", match.PatternName);
            Assert.Equal(input, match.MatchedText);
        }

        [Fact]
        public void Scan_GitHubPat_Detected()
        {
            var input = "ghp_" + new string('A', 36);
            var results = SecretScanner.Scan(input);
            Assert.Single(results);
            var match = results[0];
            Assert.Equal("GitHub PAT", match.PatternName);
            Assert.Equal(input, match.MatchedText);
        }

        [Fact]
        public void Scan_AnthropicApiKey_Detected()
        {
            var input = "sk-ant-api03-" + new string('A', 93);
            var results = SecretScanner.Scan(input);
            Assert.Single(results);
            var match = results[0];
            Assert.Equal("Anthropic API Key", match.PatternName);
            Assert.Equal(input, match.MatchedText);
        }

        [Fact]
        public void Scan_GenericSecret_Detected()
        {
            var input = "secret:ABCDEFGHIJKLMNOP";
            var results = SecretScanner.Scan(input);
            Assert.Single(results);
            var match = results[0];
            Assert.Equal("Generic Secret", match.PatternName);
            Assert.Equal(input, match.MatchedText);
        }

        [Theory]
        [InlineData("no secrets here")]
        [InlineData("just some text")]
        [InlineData("1234567890")]
        public void Scan_NoMatch_ReturnsEmpty(string input)
        {
            var results = SecretScanner.Scan(input);
            Assert.Empty(results);
        }

        [Fact]
        public void Scan_ReturnsMatchesInAscendingOrder()
        {
            var jwt = "ey" + new string('a', 17) + ".ey" + new string('a', 17) + ".sig";
            var aws = "AKIA" + new string('A', 16);
            var input = jwt + " " + aws;

            var results = SecretScanner.Scan(input);
            Assert.Equal(2, results.Count);
            Assert.Equal("JWT Token", results[0].PatternName);
            Assert.Equal("AWS Access Key", results[1].PatternName);
        }

        [Theory]
        [InlineData("~/.ssh/id_rsa")]
        [InlineData("~/.aws/credentials")]
        [InlineData("cat .env")]
        [InlineData("path/to/secrets.yaml")]
        public void ContainsSensitivePath_PositiveCases(string input)
        {
            Assert.True(SecretScanner.ContainsSensitivePath(input));
        }

        [Theory]
        [InlineData("ls /tmp")]
        [InlineData("cat README.md")]
        [InlineData("echo hello")]
        public void ContainsSensitivePath_NegativeCases(string input)
        {
            Assert.False(SecretScanner.ContainsSensitivePath(input));
        }
    }
}
