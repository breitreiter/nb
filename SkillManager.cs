using Spectre.Console;
using nb.Utilities;

namespace nb;

public record Skill(string Name, string Description, string[] Keywords, string[] Phrases, string Body);

public class SkillManager
{
    private readonly List<Skill> _skills = new();
    private string? _activeSkillName;
    private static readonly string SkillsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nb", "skills");

    public string? ActiveSkillName => _activeSkillName;
    public IReadOnlyList<Skill> Skills => _skills;

    public void LoadAllSkills()
    {
        _skills.Clear();

        if (!Directory.Exists(SkillsDirectory))
        {
            Directory.CreateDirectory(SkillsDirectory);
            return;
        }

        foreach (var dir in Directory.GetDirectories(SkillsDirectory))
        {
            var skillFile = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(skillFile))
                continue;

            var skill = ParseSkillFile(skillFile);
            if (skill != null)
                _skills.Add(skill);
        }
    }

    public Skill? ParseSkillFile(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var (frontmatter, body) = ParseFrontmatter(content);

        if (frontmatter == null || string.IsNullOrWhiteSpace(body))
            return null;

        if (!frontmatter.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            return null;
        if (!frontmatter.TryGetValue("description", out var description) || string.IsNullOrWhiteSpace(description))
            return null;
        if (!frontmatter.TryGetValue("keywords", out var keywordsRaw) || string.IsNullOrWhiteSpace(keywordsRaw))
            return null;

        var keywords = keywordsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var phrases = Array.Empty<string>();
        if (frontmatter.TryGetValue("phrases", out var phrasesRaw) && !string.IsNullOrWhiteSpace(phrasesRaw))
            phrases = phrasesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new Skill(name.Trim(), description.Trim(), keywords, phrases, body.Trim());
    }

    public static (Dictionary<string, string>? Frontmatter, string Body) ParseFrontmatter(string content)
    {
        var lines = content.Split('\n');

        // Must start with ---
        if (lines.Length < 3 || lines[0].Trim() != "---")
            return (null, content);

        var frontmatter = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int closingIndex = -1;

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                closingIndex = i;
                break;
            }

            var colonIndex = lines[i].IndexOf(':');
            if (colonIndex > 0)
            {
                var key = lines[i][..colonIndex].Trim();
                var value = lines[i][(colonIndex + 1)..].Trim();
                frontmatter[key] = value;
            }
        }

        if (closingIndex < 0)
            return (null, content);

        var body = string.Join('\n', lines[(closingIndex + 1)..]);
        return (frontmatter, body);
    }

    public List<(Skill Skill, int Score)> FindMatchingSkills(string userInput, int threshold = 3)
    {
        var input = userInput.ToLowerInvariant();
        var results = new List<(Skill, int)>();

        foreach (var skill in _skills)
        {
            int score = 0;

            foreach (var keyword in skill.Keywords)
            {
                if (input.Contains(keyword.ToLowerInvariant()))
                    score += 1;
            }

            foreach (var phrase in skill.Phrases)
            {
                if (input.Contains(phrase.ToLowerInvariant()))
                    score += 2;
            }

            if (score >= threshold)
                results.Add((skill, score));
        }

        results.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return results;
    }

    public string? GetSkillBody(string name)
    {
        return _skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Body;
    }

    public bool LoadSkill(string name)
    {
        var skill = _skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (skill == null)
            return false;

        _activeSkillName = skill.Name;
        return true;
    }

    public void UnloadSkill()
    {
        _activeSkillName = null;
    }
}
