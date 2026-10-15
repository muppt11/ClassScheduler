using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RoomScheduler
{
    /// <summary>One row of the professor scheduling-preference form, as submitted.</summary>
    public class PreferenceForm
    {
        public string Submitted { get; init; } = "";
        public string Professor { get; init; } = "";
        public string Course { get; init; } = "";
        public string Time1 { get; init; } = "";
        public string Time2 { get; init; } = "";
        public string Time3 { get; init; } = "";
        public string TimeOther { get; init; } = "";
        public string RoomRequest { get; init; } = "";
        public string Area { get; init; } = "";
        public string Requirements { get; init; } = "";
        public string Discussion { get; init; } = "";
        public string DiscussionRequirements { get; init; } = "";
        public string CrossListed { get; init; } = "";
        public string CrossListedWith { get; init; } = "";
        public string Comments { get; init; } = "";

        public string Surname => Names.Surname(Professor);
        public string CourseNumber => Names.CourseNumber(Course);
    }

    /// <summary>A time a section could meet, with how much the professor wants it (1 is first choice).</summary>
    public record TimeOption(MeetingTime Time, int Rank, string Source);

    /// <summary>What the form asks for, in terms the scheduler can act on.</summary>
    public class Wishes
    {
        public List<TimeOption> Times { get; } = new();
        public List<(MeetingTime Time, string Quote)> Blocked { get; } = new();

        public List<string> Rooms { get; } = new();          // "GDC 5.302"
        public List<string> Buildings { get; } = new();      // "GDC"
        public List<string> FloorPrefixes { get; } = new();  // "GDC 4."
        public List<string> AvoidBuildings { get; } = new();
        public List<string> AvoidRoomPrefixes { get; } = new(); // "RLP 0.", "FAC 21"
        public HashSet<string> Features { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool NearGdc { get; set; }

        public bool BackToBack { get; set; }
        public bool SameRoom { get; set; }
        public bool Online { get; set; }
        public bool WantsDiscussion { get; set; }

        /// <summary>Asked for lectures to be recorded.</summary>
        public bool Recording { get; set; }

        /// <summary>Things the professor says about past semesters, checked later against the record.</summary>
        public List<string> HistoryClaims { get; } = new();

        public List<ReviewFlag> Flags { get; } = new();
    }

    public static class PreferenceParser
    {
        // Days are matched case-sensitively so a stray "t" or "w" in a word is not a day.
        private const string DayPattern =
            @"(?<![A-Za-z])(MTWTH|MTWTh|MTWR|MWF|MW|MF|TTH|TTh|TR|T-Th|T/Th|TuTh|M|T|W|TH|Th|R|F|" +
            @"(?i:mondays?|tuesdays?|wednesdays?|thursdays?|fridays?|mon|tues?|wed|thurs?|fri))(?![A-Za-z])";

        private static readonly Regex DayRegex = new(DayPattern);

        private const string Ampm = @"(?:[ap]\.?\s?m\.?)";

        private static readonly Regex RangeRegex = new(
            @"(?<sh>\d{1,2})(?::(?<sm>\d{2}))?\s*(?<sap>" + Ampm + @")?\s*(?:-{1,2}|–|\bto\b)\s*" +
            @"(?<eh>\d{1,2})(?::(?<em>\d{2}))?\s*(?<eap>" + Ampm + @")?",
            RegexOptions.IgnoreCase);

        private static readonly Regex AtRegex = new(
            @"\bat\s+(?<sh>\d{1,2})(?::(?<sm>\d{2}))?\s*(?<sap>" + Ampm + ")",
            RegexOptions.IgnoreCase);

        private static readonly Regex AvailableBeforeAfter = new(
            @"available\s+(?<days>[A-Za-z/,\s]{0,20}?)\s*(?<dir>before|after)\s+(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>" + Ampm + ")?",
            RegexOptions.IgnoreCase);

        private static readonly Regex Negation = new(
            @"\b(?:not|no|avoid|never|n't)\b(?!\s+(?:far|too|only|preference|problem))|n't\b",
            RegexOptions.IgnoreCase);

        private static readonly Regex SpecificRoom = new(@"\b(?<b>[A-Z]{2,4})\s+(?<r>\d\.\d{3}[A-Z]?|\d{3}[A-Z]?)(?![\d.])");
        private static readonly Regex FloorWish = new(@"\b(?<b>[A-Z]{3})\s+(?<f>\d)(?![\d.])");
        private static readonly Regex FloorAnything = new(@"\b(?<b>[A-Z]{3})\s+(?<f>\d)\.(?:anything|x+|\*)", RegexOptions.IgnoreCase);
        private static readonly Regex OddRoom = new(@"\b(?<b>[A-Z]{3})\s+(?<r>\d{2})\b");

        private static readonly Regex HistoryClaim = new(
            @"(same as (?:fall|spring|last)[^.]*|as usual|previous years?|past years?|where I am teaching[^)]*|" +
            @"(?:what|time) I (?:have|am teaching) this semester|in the past|been done in the past|like in the previous[^.]*)",
            RegexOptions.IgnoreCase);

        private static readonly Regex Placeholder = new(
            @"ignore (?:the|what|my) |ignore the (?:times|choices)|place ?holder|had to fill in",
            RegexOptions.IgnoreCase);

        private static readonly Regex Pleasantry = new(
            @"^\s*(?:thank(?:s| you)[^.]*|none|n/?a|no|face[- ]to[- ]face|class will be in person|i would like to teach the class face-to-face)[.!]*\s*$",
            RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, string> RequirementCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Media Facilities"] = "CON",
            ["Screen"] = "CON",
            ["Continuous Writing Surface"] = "CWS",
            ["Movable Chairs"] = "MC",
            ["Seminar Room"] = "SEM",
            ["Movable Tables"] = "TBL",
        };

        /// <summary>
        /// Every day-plus-time found in a piece of text, in order. A range with no
        /// days in front of it uses <paramref name="fallbackDays"/> or is skipped.
        /// A single start time ("Fridays at 2pm") needs <paramref name="defaultDuration"/>.
        /// A window longer than the default duration ("any time between 9am-4pm")
        /// is expanded into every hourly start that fits.
        /// </summary>
        public static List<(MeetingTime Time, string Quote)> FindTimes(
            string text, TimeSpan? defaultDuration, IReadOnlyList<DayOfWeek>? fallbackDays = null)
        {
            var found = new List<(MeetingTime, string)>();
            if (string.IsNullOrWhiteSpace(text)) return found;

            text = Regex.Replace(text, @"\bnoon\b", "12:00", RegexOptions.IgnoreCase);

            var matches = RangeRegex.Matches(text).Cast<Match>()
                .Select(m => (Match: m, Single: false))
                .Concat(AtRegex.Matches(text).Cast<Match>().Select(m => (Match: m, Single: true)))
                .OrderBy(x => x.Match.Index)
                .ToList();

            int lastEnd = 0;
            foreach (var (m, single) in matches)
            {
                if (m.Index < lastEnd) continue;

                string before = text.Substring(lastEnd, m.Index - lastEnd);
                var dayMatch = DayRegex.Matches(before).Cast<Match>().LastOrDefault();
                IReadOnlyList<DayOfWeek>? days = dayMatch != null ? ParseDayToken(dayMatch.Value) : fallbackDays;
                lastEnd = m.Index + m.Length;

                if (days == null || days.Count == 0) continue;

                var start = ToClock(m.Groups["sh"].Value, m.Groups["sm"].Value, m.Groups["sap"].Value, null);
                if (start == null) continue;

                if (single)
                {
                    if (defaultDuration == null) continue;
                    found.Add((new MeetingTime(days, start.Value, start.Value + defaultDuration.Value), m.Value.Trim()));
                    continue;
                }

                var end = ToClock(m.Groups["eh"].Value, m.Groups["em"].Value, m.Groups["eap"].Value, null);
                if (end == null) continue;

                // An unmarked start that would come after a pm end is a morning start: "11-2".
                if (end <= start && start.Value.Hours >= 12 && string.IsNullOrEmpty(m.Groups["sap"].Value))
                    start = start.Value - TimeSpan.FromHours(12);

                var span = end.Value - start.Value;
                if (span < TimeSpan.FromMinutes(30) || span > TimeSpan.FromHours(10)) continue;

                string quote = (dayMatch?.Value + " " + m.Value).Trim();

                if (defaultDuration != null && span > defaultDuration.Value + TimeSpan.FromMinutes(30))
                {
                    for (var s = start.Value; s + defaultDuration.Value <= end.Value; s += TimeSpan.FromHours(1))
                        found.Add((new MeetingTime(days, s, s + defaultDuration.Value), quote));
                }
                else
                {
                    found.Add((new MeetingTime(days, start.Value, end.Value), quote));
                }
            }

            return found;
        }

        /// <summary>One cell of the form, such as "TTH 12:30-2:00". Null for placeholders like "MTWTH".</summary>
        public static MeetingTime? ParseChoice(string cell) =>
            FindTimes(cell, null).Select(t => t.Time).FirstOrDefault();

        /// <summary>
        /// Reads clock text the way faculty write it. With no am/pm, 1 to 7 is
        /// afternoon and 8 to 12 is morning or noon, because nothing here starts
        /// before 8am.
        /// </summary>
        private static TimeSpan? ToClock(string hourText, string minuteText, string ampm, bool? forcePm)
        {
            if (!int.TryParse(hourText, out int h)) return null;
            int m = int.TryParse(minuteText, out int mm) ? mm : 0;
            if (h > 23 || m > 59) return null;

            string ap = ampm.Replace(".", "").Replace(" ", "").ToLowerInvariant();
            if (ap == "pm" || forcePm == true) { if (h < 12) h += 12; }
            else if (ap == "am") { if (h == 12) h = 0; }
            else if (h >= 1 && h <= 7) h += 12;

            return new TimeSpan(h, m, 0);
        }

        private static List<DayOfWeek> ParseDayToken(string token)
        {
            string t = token.ToUpperInvariant().Replace("-", "").Replace("/", "");
            if (t.StartsWith("MON")) return new() { DayOfWeek.Monday };
            if (t.StartsWith("TUES") || t == "TUE") return new() { DayOfWeek.Tuesday };
            if (t.StartsWith("WED")) return new() { DayOfWeek.Wednesday };
            if (t.StartsWith("THUR")) return new() { DayOfWeek.Thursday };
            if (t.StartsWith("FRI")) return new() { DayOfWeek.Friday };
            if (t == "TUTH") t = "TTH";
            if (t == "MTWR") t = "MTWTH";
            if (t == "TR") t = "TTH";
            if (t == "R") t = "TH";
            return MeetingTime.ParseDays(t);
        }

        /// <summary>
        /// Expands "MTWTh 1 hour slot, afternoons preferred" into every afternoon
        /// hour. Used only when the note names days and a part of the day but no clock time.
        /// </summary>
        private static List<MeetingTime> PartOfDay(string text)
        {
            var result = new List<MeetingTime>();
            var dayMatch = DayRegex.Match(text);
            if (!dayMatch.Success) return result;

            var days = ParseDayToken(dayMatch.Value);
            if (days.Count == 0) return result;

            var duration = Regex.IsMatch(text, @"1\s*hour|one hour|hour (?:slot|block)", RegexOptions.IgnoreCase)
                ? TimeSpan.FromHours(1)
                : (TimeSpan?)null;
            if (duration == null) return result;

            int[] starts =
                Regex.IsMatch(text, @"late morning", RegexOptions.IgnoreCase) ? new[] { 10, 11 } :
                Regex.IsMatch(text, @"morning", RegexOptions.IgnoreCase) ? new[] { 9, 10, 11, 8 } :
                Regex.IsMatch(text, @"afternoon", RegexOptions.IgnoreCase) ? new[] { 13, 14, 15, 16 } :
                Array.Empty<int>();

            foreach (int h in starts)
                result.Add(new MeetingTime(days, TimeSpan.FromHours(h), TimeSpan.FromHours(h) + duration.Value));
            return result;
        }

        /// <summary>Turns the raw form into time options, blocked times, room wishes and flags.</summary>
        public static Wishes Interpret(PreferenceForm form, Campus campus, ICollection<Room> rooms)
        {
            var w = new Wishes();
            string allNotes = $"{form.TimeOther} {form.Comments}";

            // ---- times
            bool threeHourBlock = new[] { form.Time1, form.Time2, form.Time3 }
                .Any(c => c.Contains("3 hours", StringComparison.OrdinalIgnoreCase));
            TimeSpan? noteDuration = threeHourBlock ? TimeSpan.FromHours(3) : null;

            var choices = new[] { form.Time1, form.Time2, form.Time3 }
                .Select((c, i) => (Time: ParseChoice(c), Rank: i + 1))
                .Where(c => c.Time != null)
                .Select(c => new TimeOption(c.Time!, c.Rank, $"choice {c.Rank}"))
                .ToList();

            // Times found in one stretch of note text, like every hour of "any time
            // between 9am-4pm" or every afternoon hour, are equally good to the
            // professor, so they share a rank.
            // Sentences saying what they cannot do are read separately below.
            string positiveOther = string.Join(". ", Sentences(form.TimeOther).Where(s => !SaysCannot(s)));
            var noteFound = FindTimes(positiveOther, noteDuration);
            var noteTimes = noteFound.Select(t => t.Time).ToList();
            var noteGroups = noteFound.Select(t => t.Quote).ToList();
            if (noteTimes.Count == 0)
            {
                noteTimes = PartOfDay(form.TimeOther);
                noteGroups = noteTimes.Select(_ => "part of day").ToList();
            }

            // "I only plan to meet on Fridays" narrows every choice to that day.
            var onlyDay = Regex.Match(allNotes, @"(?:only|preferably)[^.]{0,30}?\b(?:on\s+)?(?<d>mondays?|tuesdays?|wednesdays?|thursdays?|fridays?)\b", RegexOptions.IgnoreCase);
            if (onlyDay.Success)
            {
                var day = ParseDayToken(onlyDay.Groups["d"].Value);
                choices = choices
                    .Where(c => c.Time.Days.Intersect(day).Any())
                    .Select(c => c with { Time = new MeetingTime(day, c.Time.Start, c.Time.End) })
                    .ToList();
                w.Flags.Add(new ReviewFlag(FlagLevel.Info, ReviewFlag.Time,
                    $"Notes say they meet only on {day[0]}s; choices narrowed to that day."));
            }

            bool placeholder = Placeholder.IsMatch(allNotes);
            bool identicalChoices = choices.Count == 3 && choices.Select(c => c.Time).Distinct().Count() == 1;
            bool notesFirst = noteTimes.Count > 0 &&
                              (placeholder || identicalChoices || choices.Count == 0 ||
                               Regex.IsMatch(form.TimeOther, @"prefer|ideal|1st choice", RegexOptions.IgnoreCase));

            if (placeholder && noteTimes.Count > 0 && choices.Count > 0)
                w.Flags.Add(new ReviewFlag(FlagLevel.Info, ReviewFlag.Contradiction,
                    "Form choices were placeholders (the notes say so); scheduled from the notes instead: \"" +
                    Trim(form.TimeOther) + "\""));
            else if (identicalChoices && noteTimes.Count > 0)
                w.Flags.Add(new ReviewFlag(FlagLevel.Info, ReviewFlag.Contradiction,
                    $"All three choices are the same ({choices[0].Time}) but the notes ask for something else; using the notes first."));
            else if (placeholder && choices.Count > 0)
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.Contradiction,
                    "Notes say the form choices are placeholders, but give no usable time: \"" + Trim(allNotes) + "\""));

            int rank = 1;
            var ordered = new List<TimeOption>();

            void AddNotes()
            {
                for (int i = 0; i < noteTimes.Count; i++)
                {
                    if (i > 0 && noteGroups[i] != noteGroups[i - 1]) rank++;
                    ordered.Add(new TimeOption(noteTimes[i], rank, "notes"));
                }
                if (noteTimes.Count > 0) rank++;
            }

            if (notesFirst)
            {
                AddNotes();
                if (!placeholder)
                    foreach (var c in choices) ordered.Add(c with { Rank = rank++ });
            }
            else
            {
                foreach (var c in choices) ordered.Add(c with { Rank = rank++ });
                AddNotes();
            }

            foreach (var opt in ordered)
                if (!w.Times.Any(t => t.Time.Equals(opt.Time)))
                    w.Times.Add(opt);

            // ---- times they say they cannot do
            var choiceDays = choices.SelectMany(c => c.Time.Days).Distinct().ToList();
            foreach (var sentence in Sentences(allNotes))
            {
                var avail = AvailableBeforeAfter.Match(sentence);
                if (avail.Success)
                {
                    var days = DayRegex.Matches(avail.Groups["days"].Value).Cast<Match>()
                        .SelectMany(d => ParseDayToken(d.Value)).Distinct().ToList();
                    if (days.Count == 0) days = choiceDays;
                    var at = ToClock(avail.Groups["h"].Value, avail.Groups["m"].Value, avail.Groups["ap"].Value, null);
                    if (at != null && days.Count > 0)
                    {
                        var block = avail.Groups["dir"].Value.Equals("before", StringComparison.OrdinalIgnoreCase)
                            ? new MeetingTime(days, at.Value, new TimeSpan(22, 0, 0))
                            : new MeetingTime(days, new TimeSpan(7, 0, 0), at.Value);
                        w.Blocked.Add((block, Trim(sentence)));
                    }
                    continue;
                }

                if (!SaysCannot(sentence))
                    continue;

                foreach (var (t, _) in FindTimes(sentence, null, choiceDays))
                    w.Blocked.Add((t, Trim(sentence)));
            }

            // A choice that breaks the professor's own stated limit is dropped and flagged.
            foreach (var opt in w.Times.ToList())
            {
                var clash = w.Blocked.FirstOrDefault(b => b.Time.Overlaps(opt.Time));
                if (clash.Time == null) continue;
                w.Times.Remove(opt);
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.Contradiction,
                    $"Listed {opt.Time} ({opt.Source}) but also wrote \"{clash.Quote}\". Dropped that time."));
            }

            // ---- rooms and buildings
            ReadRoomText(form.RoomRequest, w, campus, rooms);
            ReadRoomText(form.Area, w, campus, rooms);

            string place = $"{form.RoomRequest} {form.Area}";
            if (Regex.IsMatch(place, @"(near|close to|closer to|south of|between|central|not far from)[^.]*gdc|gdc[^.]*(near|close)", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(form.Area, @"\bgdc\b", RegexOptions.IgnoreCase))
            {
                w.NearGdc = true;
                if (!w.Buildings.Contains("GDC")) w.Buildings.Add("GDC");
            }

            if (Regex.IsMatch(place, @"garage|museum|newer building|basement|\d(?:st|nd|rd|th) floor", RegexOptions.IgnoreCase))
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.Room,
                    $"Location wish the app can only partly follow: \"{Trim(place)}\""));

            if (Regex.IsMatch(place + " " + form.Comments, @"awful|cramped|terrible|hate|too small|bad room", RegexOptions.IgnoreCase))
                w.Flags.Add(new ReviewFlag(FlagLevel.Info, ReviewFlag.SpecialRequest,
                    $"Room complaint, worth adding to their profile: \"{Trim(place + " " + form.Comments)}\""));

            foreach (var req in form.Requirements.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (RequirementCodes.TryGetValue(req, out var code))
                    w.Features.Add(code);

            if (Regex.IsMatch(form.Comments, @"white ?board", RegexOptions.IgnoreCase))
                w.Features.Add("Wb");

            // ---- everything else in the notes
            var handled = new List<string>();

            if (Regex.IsMatch(allNotes, @"back[- ]to[- ]back|consecutive", RegexOptions.IgnoreCase))
            {
                w.BackToBack = true;
                handled.Add("back-to-back");
            }

            if (Regex.IsMatch(allNotes + " " + place, @"same (?:room|classroom|location)", RegexOptions.IgnoreCase))
            {
                w.SameRoom = true;
                handled.Add("same room");
            }

            if (Regex.IsMatch(form.Course, @"-WB\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(allNotes, @"no classroom for web|taught as a web", RegexOptions.IgnoreCase))
            {
                w.Online = true;
                handled.Add("online");
            }

            if (Regex.IsMatch(allNotes + " " + form.RoomRequest, @"record|lectures? online|online capabilit", RegexOptions.IgnoreCase))
            {
                w.Recording = true;
                handled.Add("recording");
            }

            if (Regex.IsMatch(allNotes, @"shouldn.t be offering|holding off|do not plan to teach|won.t (?:be )?teach", RegexOptions.IgnoreCase))
            {
                w.Flags.Add(new ReviewFlag(FlagLevel.Problem, ReviewFlag.SpecialRequest,
                    $"Says they may not teach this: \"{Trim(allNotes)}\""));
                handled.Add("not teaching");
            }

            if (Regex.IsMatch(allNotes, @"textbook|isbn|order a", RegexOptions.IgnoreCase))
            {
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.SpecialRequest,
                    $"Request that is not about scheduling (forward it): \"{Trim(form.Comments)}\""));
                handled.Add("other request");
            }

            if (Regex.IsMatch(allNotes, @"avoid conflicts with|not conflict with other", RegexOptions.IgnoreCase))
            {
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.SpecialRequest,
                    $"Wants no overlap with related courses; check by hand: \"{Trim(allNotes)}\""));
                handled.Add("avoid overlap");
            }

            if (Regex.IsMatch(form.Comments + " " + form.TimeOther, @"(screen|projector)[^.]*(white|chalk|black)board|board[^.]*(screen|projector)", RegexOptions.IgnoreCase))
            {
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.Room,
                    "Needs screen and board usable at the same time. The room table does not record this, so check by hand."));
                handled.Add("screen and board");
            }

            if (Regex.IsMatch(form.Comments + " " + form.TimeOther, @"hybrid", RegexOptions.IgnoreCase))
            {
                w.Flags.Add(new ReviewFlag(FlagLevel.Info, ReviewFlag.SpecialRequest, "Hybrid course."));
                handled.Add("hybrid");
            }

            if (form.Discussion.StartsWith("Y", StringComparison.OrdinalIgnoreCase) ||
                (form.DiscussionRequirements.Trim().Length > 0 && !Pleasantry.IsMatch(form.DiscussionRequirements)))
            {
                w.WantsDiscussion = form.Discussion.StartsWith("Y", StringComparison.OrdinalIgnoreCase);
                string detail = form.DiscussionRequirements.Trim().Length > 0 ? $": \"{Trim(form.DiscussionRequirements)}\"" : "";
                w.Flags.Add(new ReviewFlag(FlagLevel.Info, ReviewFlag.SpecialRequest,
                    $"Discussion section requested{detail}."));
            }

            if (form.CrossListed.StartsWith("Y", StringComparison.OrdinalIgnoreCase))
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.SpecialRequest,
                    $"Cross-listed with {Fallback(form.CrossListedWith, "another department")}. Coordinate the time and room with them."));

            foreach (Match claim in HistoryClaim.Matches($"{allNotes} {form.RoomRequest} {form.DiscussionRequirements}"))
                w.HistoryClaims.Add(claim.Value.Trim());

            // Notes nothing above accounted for go to a person.
            bool notesUsed = noteTimes.Count > 0 || w.Blocked.Count > 0 || placeholder;
            string otherLeft = notesUsed || handled.Count > 0 || w.HistoryClaims.Count > 0 ? "" : form.TimeOther;
            foreach (var leftover in new[] { otherLeft, form.Comments })
            {
                if (string.IsNullOrWhiteSpace(leftover) || Pleasantry.IsMatch(leftover)) continue;
                if (leftover == form.Comments && (handled.Count > 0 || w.Blocked.Count > 0)) continue;
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.SpecialRequest, $"Note to read: \"{Trim(leftover)}\""));
            }

            // The same note often sits in both "Time other" and "comments".
            var unique = w.Flags.DistinctBy(f => f.Message).ToList();
            w.Flags.Clear();
            w.Flags.AddRange(unique);
            return w;
        }

        private static void ReadRoomText(string text, Wishes w, Campus campus, ICollection<Room> rooms)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (Regex.IsMatch(text.Trim(), @"^(no preference|none|no|n/a|similar to previous years\.?)$", RegexOptions.IgnoreCase))
                return;

            text = text.Replace("!.", "1.");

            foreach (var clause in Regex.Split(text, @"(?<!\d)\.(?!\d)|[;!?]|\bbut\b"))
            {
                var neg = Negation.Match(clause);
                int negAt = neg.Success ? neg.Index : int.MaxValue;

                foreach (Match m in FloorAnything.Matches(clause))
                {
                    var prefix = $"{m.Groups["b"].Value} {m.Groups["f"].Value}.";
                    (m.Index > negAt ? w.AvoidRoomPrefixes : w.FloorPrefixes).Add(prefix);
                }

                foreach (Match m in SpecificRoom.Matches(clause))
                {
                    string name = $"{m.Groups["b"].Value} {m.Groups["r"].Value}";
                    if (m.Index > negAt) { w.AvoidRoomPrefixes.Add(name); continue; }

                    w.Rooms.Add(name);
                    if (!rooms.Any(r => r.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.Room,
                            $"Asked for {name}, which is not in the room list. Typo, or a room the department does not schedule?"));
                }

                foreach (Match m in OddRoom.Matches(clause))
                    if (m.Index > negAt) w.AvoidRoomPrefixes.Add($"{m.Groups["b"].Value} {m.Groups["r"].Value}");

                foreach (Match m in FloorWish.Matches(clause))
                    if (m.Index < negAt && campus.Knows(m.Groups["b"].Value))
                        w.FloorPrefixes.Add($"{m.Groups["b"].Value} {m.Groups["f"].Value}.");

                foreach (var (code, pos) in campus.FindBuildings(clause))
                {
                    var list = pos > negAt ? w.AvoidBuildings : w.Buildings;
                    bool roomLevel = w.AvoidRoomPrefixes.Any(p => p.StartsWith(code + " ")) && pos > negAt;
                    if (!roomLevel && !list.Contains(code)) list.Add(code);
                }
            }

            // "NOT RLP 0.anything" avoids a floor, not the whole building.
            w.AvoidBuildings.RemoveAll(b => w.AvoidRoomPrefixes.Any(p => p.StartsWith(b + " ")) || w.Buildings.Contains(b));

            foreach (var b in w.Buildings.Where(b => !rooms.Any(r => r.Building.Equals(b, StringComparison.OrdinalIgnoreCase))))
                w.Flags.Add(new ReviewFlag(FlagLevel.Review, ReviewFlag.Room,
                    $"Asked for {b}, which has no rooms in the room list" +
                    (campus.Names.TryGetValue(b, out var n) ? $" ({n})." : ".")));
        }

        /// <summary>
        /// "Please do not give me Wed noon-1PM" says what they cannot do.
        /// "If I can't have 1-2, 2-3 would be the best alternative" does not.
        /// </summary>
        private static bool SaysCannot(string sentence) =>
            Regex.IsMatch(sentence, @"\b(?:not|avoid|prevent\w*|clash\w*|overlap\w*|conflict\w*|can't|cannot)\b", RegexOptions.IgnoreCase) &&
            !Regex.IsMatch(sentence, @"^\s*if\b", RegexOptions.IgnoreCase);

        private static IEnumerable<string> Sentences(string text) =>
            Regex.Split(text, @"(?<!\d)\.(?!\d)|[!?;]").Where(s => s.Trim().Length > 0);

        private static string Trim(string s)
        {
            s = Regex.Replace(s.Trim(), @"\s+", " ");
            return s.Length > 220 ? s.Substring(0, 217) + "..." : s;
        }

        private static string Fallback(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();
    }
}
