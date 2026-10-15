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

        /// <summary>"D" on the sheet. Difficult faculty get first pick.</summary>
        public bool Difficult { get; set; }

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

        public IEnumerable<FacultyProfile> All => _profiles.Values.OrderBy(p => p.Surname);

        public FacultyProfile? Find(string surname) =>
            _profiles.TryGetValue(surname, out var p) ? p : null;

        public FacultyProfile GetOrAdd(string surname, string displayName)
        {
            lock (_lock)
            {
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
            if (_save == null) return;
            lock (_lock)
                _save(JsonSerializer.Serialize(All.ToList(), Json));
        }
    }
}
