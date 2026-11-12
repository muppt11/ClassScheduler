using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler;

public sealed partial class SchedulingConversation
{
    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16, ["seventeen"] = 17,
        ["eighteen"] = 18, ["nineteen"] = 19, ["twenty"] = 20, ["thirty"] = 30,
        ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70,
        ["eighty"] = 80, ["ninety"] = 90
    };

    private static string NormalizeCapacityLanguage(string text)
    {
        // Restrict number-word conversion to capacity phrases so course titles stay intact.
        string units = "(?:" + string.Join("|", NumberWords.Where(p => p.Value is > 0 and < 10).Select(p => p.Key)) + ")";
        string teens = "(?:" + string.Join("|", NumberWords.Where(p => p.Value is >= 10 and < 20).Select(p => p.Key)) + ")";
        string tens = "(?:" + string.Join("|", NumberWords.Where(p => p.Value >= 20).Select(p => p.Key)) + ")";
        string small = "(?:" + tens + "(?:[ -]+" + units + ")?|" + teens + "|" + units + "|zero)";
        string hundreds = "(?:" + units + "[ -]+hundred(?:[ -]+(?:and\\s+)?" + small + ")?|" + small + ")";
        string number = @"(?:\d+(?:,\d{3})*|" + hundreds + "(?:[ -]+thousand(?:[ -]+(?:and\\s+)?" + hundreds + ")?)?)";
        text = Regex.Replace(text, @"\b(?:between|from)\s+(" + number + @")\s+(?:and|to)\s+(" + number + @")\s*(?:seats?|people|students?)\b",
            m => "at least " + CapacityNumber(m.Groups[1].Value) + " seats and at most " + CapacityNumber(m.Groups[2].Value) + " seats", Ignore);
        text = Regex.Replace(text, @"\b(" + number + @")(?=\s*(?:-\s*seat\b|seats?\b|people\b|students?\b))", m => CapacityNumber(m.Value), Ignore);
        text = Regex.Replace(text, @"\b(?:can\s+)?(?:fit|fits|hold|holds|seat|seats|accommodate|accommodates)\s+(" + number + @")(?:\s+(?:people|students?|seats?))?\b",
            m => CapacityNumber(m.Groups[1].Value) + " seats", Ignore);
        text = Regex.Replace(text, @"\b(?:capacity\s+(?:of\s+)?|(?:space|seating)\s+for\s+)(" + number + @")(?:\s+(?:people|students?|seats?))?\b",
            m => CapacityNumber(m.Groups[1].Value) + " seats", Ignore);
        text = Regex.Replace(text, @"\b(\d+)\s*-\s*seat\b", "$1 seats", Ignore);
        text = Regex.Replace(text, @"\b(?:between\s+|from\s+)?(\d+)\s*(?:-|–|to)\s*(\d+)\s*(?:seats?|people|students?)\b", "at least $1 seats and at most $2 seats", Ignore);
        text = Regex.Replace(text, @"\b(?:minimum|min|no fewer than)\s+(\d+)\s*(seats?|people|students?)\b", "at least $1 $2", Ignore);
        text = Regex.Replace(text, @"\b(?:maximum|max|up to|not more than)\s+(\d+)\s*(seats?|people|students?)\b", "at most $1 $2", Ignore);
        text = Regex.Replace(text, @"\b(\d+)\s*\+\s*(seats?|people|students?)\b", "at least $1 $2", Ignore);
        text = Regex.Replace(text, @"\b(\d+)\s*(seats?|people|students?)\s*(?:min|minimum|or more|or greater)\b", "at least $1 $2", Ignore);
        return text;
    }

    private static string NormalizeRequestLanguage(string text)
    {
        text = Regex.Replace(text, @"\b(?:do not|don't|dont)\s+(?:change|move)\s+(?:(?:the|its|my)\s+)?(?:meeting\s+)?(time|room)\b", "keep the same $1", Ignore);
        text = Regex.Replace(text, @"\bleave\s+(?:(?:the|all)\s+)?other\s+(?:classes|sections|courses)\s+alone\b", "without moving other classes", Ignore);
        text = Regex.Replace(text, @"\b(?:(?:the\s+)?(?:meeting\s+)?time\s+(?:can change|can be changed|is flexible)|changing\s+(?:the\s+)?time\s+is\s+(?:fine|okay|ok))\b", "allow changing time", Ignore);
        return text;
    }

    private static string CapacityNumber(string text)
    {
        if (char.IsDigit(text[0])) return text.Replace(",", "");
        long total = 0, group = 0;
        foreach (string word in Regex.Split(text.ToLowerInvariant(), @"[\s-]+"))
        {
            if (word == "and") continue;
            if (word == "hundred") group = Math.Max(1, group) * 100;
            else if (word == "thousand") { total += Math.Max(1, group) * 1000; group = 0; }
            else group += NumberWords[word];
        }
        return (total + group).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private string ExpandCapacityFollowup(string text)
    {
        if (_target == null || _filters.MinSeats == null && _filters.MaxSeats == null) return text;
        // Only fill in omitted units for explicit corrections, never for an option number.
        return Regex.Replace(text,
            @"^(?:(?:actually|instead|no)[, ]+)?(?:make (?:it|that)|change (?:it|that) to|I meant)\s+(?:(at least|at most|exactly|under|over)\s+)?(\d+)(?:\s+instead)?[.!?]?$",
            m => "actually " + m.Groups[1].Value + " " + m.Groups[2].Value + " seats instead", Ignore);
    }
}
