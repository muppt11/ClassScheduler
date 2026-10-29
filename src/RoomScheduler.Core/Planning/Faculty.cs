using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RoomScheduler
{
    public enum FacultyTrack
    {
        Unknown,

        /// <summary>Tenure or tenure-track ("t" on the Classes App sheet).</summary>
        Tenure,

        /// <summary>Professional-track faculty ("ptf").</summary>
        Professional
    }

    public class Complaint
    {
        public DateTime Date { get; set; } = DateTime.Today;
        public string Text { get; set; } = "";
    }

    /// <summary>
    /// What the department knows about one instructor beyond a single semester's
    /// form. Kept across runs, so the scheduler gets better each semester.
    /// </summary>
    public class FacultyProfile
    {
        /// <summary>Upper-case surname, the one key every sheet agrees on.</summary>
        public string Surname { get; set; } = "";

        public string DisplayName { get; set; } = "";

        /// <summary>Overrides the track on the Classes App sheet when set.</summary>
        public FacultyTrack Track { get; set; } = FacultyTrack.Unknown;

        /// <summary>A deletion marker prevents later imports from restoring the saved profile.</summary>
        public bool Removed { get; set; }

        /// <summary>"D" on the sheet. Difficult faculty get first pick.</summary>
        public bool Difficult { get; set; }

        /// <summary>An explicit edit, including clearing a track, takes precedence over imported values.</summary>
        public bool TrackExplicitlySet { get; set; }

        /// <summary>Distinguishes removing D from a profile that has never been edited.</summary>
        public bool DifficultExplicitlySet { get; set; }

        /// <summary>Last imported values distinguish later manual edits from reusable source data.</summary>
        public FacultyTrack? ImportedTrack { get; set; }
        public bool? ImportedDifficult { get; set; }

        internal void RememberManualChanges()
        {
            if (ImportedTrack.HasValue && Track != ImportedTrack.Value) TrackExplicitlySet = true;
            if (ImportedDifficult.HasValue && Difficult != ImportedDifficult.Value) DifficultExplicitlySet = true;
        }

        internal void PrepareForImport()
        {
            RememberManualChanges();
            if (!TrackExplicitlySet && ImportedTrack.HasValue) { Track = FacultyTrack.Unknown; ImportedTrack = FacultyTrack.Unknown; }
            if (!DifficultExplicitlySet && ImportedDifficult.HasValue) { Difficult = false; ImportedDifficult = false; }
        }


        public void SetTrack(FacultyTrack track)
        {
            Track = track;
            TrackExplicitlySet = true;
        }

        public void SetDifficult(bool difficult)
        {
            Difficult = difficult;
            DifficultExplicitlySet = true;
        }

        /// <summary>
        /// Times this person can never teach, e.g. "TTH 8:00-9:30; F 1:00-5:00".
        /// Use it for a second job, childcare, or anything else outside the department.
        /// </summary>
        public string Unavailable { get; set; } = "";

        /// <summary>Why they are unavailable, for whoever reads the profile next. Not used by the scheduler.</summary>
        public string ExternalNotes { get; set; } = "";

        // Accommodations. These are hard rules: a room that breaks one is never used.
        public bool GroundFloorOnly { get; set; }

        /// <summary>Longest walk allowed between back-to-back classes, in minutes.</summary>
        public int? MaxWalkMinutes { get; set; }

        /// <summary>Room feature codes that must be present, e.g. "CWS Wb".</summary>
        public string RequiredFeatures { get; set; } = "";

        public string AccommodationNotes { get; set; } = "";

        public List<Complaint> Complaints { get; set; } = new();

        public bool HasAccommodations =>
            GroundFloorOnly || MaxWalkMinutes != null || RequiredFeatures.Trim().Length > 0;

        public List<MeetingTime> UnavailableTimes() =>
            Unavailable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(part => PreferenceParser.FindTimes(part, defaultDuration: null))
                .Select(t => t.Time)
                .ToList();

        public HashSet<string> RequiredFeatureSet() =>
            new(RequiredFeatures.Split(new[] { ' ', ',', '/' }, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Faculty profiles, saved as JSON wherever the app keeps its settings.</summary>
    public class FacultyStore
    {
        private readonly Action<string>? _save;
        private readonly object _lock = new();
        private Dictionary<string, FacultyProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        /// <summary>
        /// Profiles from saved JSON (null for none). Save hands the JSON to
        /// <paramref name="save"/>, which the website stores in the browser.
        /// </summary>
        public FacultyStore(string? json, Action<string>? save)
        {
            _save = save;
            if (!string.IsNullOrWhiteSpace(json))
            {
                var list = JsonSerializer.Deserialize<List<FacultyProfile>>(json) ?? new();
                _profiles = list.ToDictionary(p => p.Surname, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>A store that saves nowhere, for the console app.</summary>
        public static FacultyStore InMemory() => new(null, null);

        public IEnumerable<FacultyProfile> All => _profiles.Values.Where(p => !p.Removed).OrderBy(p => p.Surname);

        public IEnumerable<FacultyProfile> RemovedProfiles => _profiles.Values.Where(p => p.Removed).OrderBy(p => p.Surname);

        /// <summary>Allow scheduling again with a fresh profile; deleted personal details are not recovered.</summary>
        public FacultyProfile AddBack(string surname)
        {
            surname = surname.Trim();
            lock (_lock)
            {
                if (!_profiles.TryGetValue(surname, out var removed) || !removed.Removed)
                    throw new ArgumentException("That professor is not in the removed list.");
                var profile = new FacultyProfile
                {
                    Surname = removed.Surname,
                    DisplayName = string.IsNullOrWhiteSpace(removed.DisplayName) ? removed.Surname : removed.DisplayName
                };
                var updated = new Dictionary<string, FacultyProfile>(_profiles, StringComparer.OrdinalIgnoreCase) { [surname] = profile };
                _save?.Invoke(JsonSerializer.Serialize(updated.Values.OrderBy(p => p.Surname).ToList(), Json));
                _profiles = updated;
                return profile;
            }
        }

        public bool IsRemoved(string surname) => _profiles.TryGetValue(surname, out var p) && p.Removed;

        internal string Snapshot() => JsonSerializer.Serialize(_profiles.Values.OrderBy(p => p.Surname).ToList(), Json);

        /// <summary>Delete personal details and persist a minimal marker before changing the live store.</summary>
        public void RemovePermanently(string surname)
        {
            surname = surname.Trim();
            lock (_lock)
            {
                if (!_profiles.TryGetValue(surname, out var profile) || profile.Removed)
                    throw new ArgumentException("That professor is no longer in the faculty list.");
                var removed = new FacultyProfile { Surname = profile.Surname, DisplayName = profile.DisplayName, Removed = true };
                var updated = new Dictionary<string, FacultyProfile>(_profiles, StringComparer.OrdinalIgnoreCase) { [surname] = removed };
                _save?.Invoke(JsonSerializer.Serialize(updated.Values.OrderBy(p => p.Surname).ToList(), Json));
                _profiles = updated;
            }
        }

        public FacultyProfile? Find(string surname) =>
            _profiles.TryGetValue(surname, out var p) && !p.Removed ? p : null;

        public FacultyProfile GetOrAdd(string surname, string displayName)
        {
            lock (_lock)
            {
                if (IsRemoved(surname)) throw new InvalidOperationException("That professor was removed. Use Add back on the Faculty page before reimporting.");
                if (!_profiles.TryGetValue(surname, out var p))
                {
                    p = new FacultyProfile { Surname = surname, DisplayName = displayName };
                    _profiles[surname] = p;
                }
                return p;
            }
        }

        public void Save()
        {
            foreach (var profile in All) profile.RememberManualChanges();
            if (_save == null) return;
            lock (_lock)
                _save(Snapshot());
        }
    }
}
