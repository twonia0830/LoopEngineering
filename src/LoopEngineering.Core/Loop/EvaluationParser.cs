using System.Text.Json;
using System.Text.RegularExpressions;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

public static partial class EvaluationParser
{
    /// <summary>
    /// Extracts the evaluator's JSON verdict. Anything unparseable counts as "criteria not met"
    /// so a confused evaluator can never end the loop early.
    /// </summary>
    public static Evaluation Parse(string raw)
    {
        foreach (var candidate in JsonCandidates(raw))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!TryGet(root, "criteriaMet", out var met)) continue;

                return new Evaluation
                {
                    CriteriaMet = met.ValueKind == JsonValueKind.True ||
                                  (met.ValueKind == JsonValueKind.String && bool.TryParse(met.GetString(), out var b) && b),
                    Score = TryGet(root, "score", out var score) && score.TryGetInt32(out var s) ? Math.Clamp(s, 0, 100) : null,
                    Reasoning = TryGet(root, "reasoning", out var r) ? r.ToString() : "",
                    NextSteps = TryGet(root, "nextSteps", out var n) ? n.ToString() : "",
                    Raw = raw
                };
            }
            catch (JsonException)
            {
                // try the next candidate
            }
        }

        return new Evaluation
        {
            CriteriaMet = false,
            Reasoning = "Evaluator reply could not be parsed as a verdict; treating criteria as not met.",
            Raw = raw
        };
    }

    private static IEnumerable<string> JsonCandidates(string raw)
    {
        foreach (Match m in FencedJson().Matches(raw))
            yield return m.Groups[1].Value;

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
            yield return raw[start..(end + 1)];
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline)]
    private static partial Regex FencedJson();
}
