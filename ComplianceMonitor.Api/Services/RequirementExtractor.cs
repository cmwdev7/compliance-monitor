using System.Text.RegularExpressions;

namespace ComplianceMonitor.Api.Services;

// Deliberately bounded grammar, not a general natural-language parser.
public static class RequirementExtractor
{
    public static string[] Extract(string guideline)
    {
        if (Regex.IsMatch(guideline, @"\b(if|unless|except|either|or)\b", RegexOptions.IgnoreCase))
            return [];
        var requirements = new List<string>();
        foreach (var sentence in Regex.Split(guideline.Trim(), @"[.;\r\n]+"))
        {
            if (string.IsNullOrWhiteSpace(sentence)) continue;
            var match = Regex.Match(sentence.Trim(), @"^(?<subject>.+?)\s+(must|shall)\s+(?<predicate>.+)$", RegexOptions.IgnoreCase);
            if (!match.Success) return [];
            var subject = match.Groups["subject"].Value;
            var predicate = match.Groups["predicate"].Value;
            // Stop lookahead at the next conjunction so later clauses cannot split object lists.
            var clauses = Regex.Split(predicate, @"\s+and\s+(?=(?:(?:(?!\band\b).)+?\s+(?:must|shall)\s+)|(?:(?:(?!\band\b)[\w -])+\s+(?:reviewed|checked|recorded|verified|inspected)\b))", RegexOptions.IgnoreCase);
            requirements.Add($"{subject} must {clauses[0]}");
            foreach (var clause in clauses.Skip(1))
            {
                if (Regex.IsMatch(clause, @"\b(must|shall)\b", RegexOptions.IgnoreCase)) requirements.Add(clause);
                else requirements.Add(Regex.Replace(clause, @"\s+(reviewed|checked|recorded|verified|inspected)\b", " must be $1", RegexOptions.IgnoreCase));
            }
        }
        return requirements.ToArray();
    }

    public static bool HasRecurrenceEvidence(string requirement, string action)
    {
        foreach (var cadence in new[] { "hour", "day", "week", "month", "year" })
        {
            var adjective = cadence switch { "day" => "daily", "year" => "annually|yearly", _ => cadence + "ly" };
            var pattern = $@"\b(?:{adjective}|every\s+(?:1\s+)?{cadence}|each\s+{cadence})\b";
            if (Regex.IsMatch(requirement, pattern, RegexOptions.IgnoreCase)
                && !Regex.IsMatch(action, pattern, RegexOptions.IgnoreCase)) return false;
        }
        foreach (Match interval in Regex.Matches(requirement, @"\bevery\s+\d+\s+(?:hours?|days?|weeks?|months?|years?)\b", RegexOptions.IgnoreCase))
            if (!Regex.IsMatch(action, Regex.Escape(interval.Value), RegexOptions.IgnoreCase)) return false;
        return true;
    }
}
