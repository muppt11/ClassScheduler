using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RoomScheduler
{
    /// <summary>What kind of file something is, judged from its contents.</summary>
    public enum FileKind
    {
        /// <summary>A semester's Classes App sheet plus preference forms: what to schedule.</summary>
        Requests,

        /// <summary>A registrar export of what actually ran (CNBR, DAYS1, BLDG1...): history.</summary>
        PastSchedule,

        /// <summary>The GPC rooms-with-features table.</summary>
        Rooms,

        /// <summary>The registrar's list of rooms that support Lectures Online (recording).</summary>
        LectureCapture,

        /// <summary>Anything else added under Additional files, kept for reference.</summary>
        Other,

        Preferences,
        Reference,
        Unknown
    }

    public record LibraryFile(string Name, FileKind Kind, string Detail, DateTime Saved);

    /// <summary>Where saved files and settings live: the browser's local storage on the website.</summary>
    public interface IKeyValueStore
    {
        string? Get(string key);
        void Set(string key, string value);
        void Remove(string key);
    }

    /// <summary>A store that keeps everything in memory, for tests.</summary>
    public class MemoryStore : IKeyValueStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => Values[key] = value;
        public void Remove(string key) => Values.Remove(key);
    }

    /// <summary>The public data files the app ships with, as text.</summary>
    public record DataFiles(string RoomsCsv, string GdcRoomsCsv, string LectureCaptureCsv, string BuildingsCsv, string CourseHistoryCsv)
    {
        /// <summary>Reads them from the repository's data folder.</summary>
        public static DataFiles FromDataFolder() => new(
            DataFile.ReadText("data/gpc_rooms_features.csv"),
            DataFile.ReadText("data/gdc_rooms.csv"),
            DataFile.ReadText("data/lecture_capture_rooms.csv"),
            DataFile.ReadText("data/buildings.csv"),
            DataFile.ReadText("data/course_history.csv"));
    }

    /// <summary>
    /// Everything the planner needs for one user: the files they added, which
    /// past semesters count as history, the room list, faculty profiles and
    /// offline rooms. All of it is saved in an <see cref="IKeyValueStore"/>, so it
    /// is still there next time. The website runs one of these in the browser.
    /// </summary>
    public partial class PlanningSession
    {
        public SchedulingConversation Conversation { get; } = new();
        public SchedulingRules Rules { get; } = new();
        public List<Room> Rooms { get; private set; } = new();
        public string RoomsSource => AddedRoomCount == 0 ? _roomsTableSource : $"{_roomsTableSource} and {AddedRoomCount} added room(s)";
        public string LectureCaptureSource { get; private set; } = "the built-in Lectures Online list";
        public Campus Campus { get; private set; } = new();
        public FacultyStore Faculty { get; private set; } = FacultyStore.InMemory();
        public CourseHistory DepartmentHistory { get; private set; } = CourseHistory.Empty;
        public int LectureCaptureRooms { get; private set; }

        public PastSchedule Past { get; private set; } = PastSchedule.Empty;

        /// <summary>Past-schedule files currently used as history. All of them unless turned off.</summary>
        public HashSet<string> ActiveHistory { get; } = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _historyTurnedOff = new(StringComparer.OrdinalIgnoreCase);

        private Planner? _planner;
        public Planner.Plan? LatestPlan { get; private set; }
        public string LatestSource { get; private set; } = "";
        private bool _inputsChanged;
        private long _inputsRevision;
        private string? _builtRules;
        public bool RequiresRebuild => LatestPlan != null && (_inputsChanged || _builtRules != JsonSerializer.Serialize(Rules));

        /// <summary>Largest file kept. Browser storage holds about 5 MB in all.</summary>
        public const int MaxFileBytes = 3 * 1024 * 1024;

        private IKeyValueStore _store = new MemoryStore();
        private DataFiles _data = new("", "", "", "", "");
        private readonly Dictionary<string, LibraryFile> _described = new(StringComparer.OrdinalIgnoreCase);

        public void Initialize(IKeyValueStore store, DataFiles data)
        {
            _store = store;
            _data = data;
            _described.Clear();

            Campus = Campus.FromCsvText(data.BuildingsCsv);
            DepartmentHistory = CourseHistory.FromCsvText(data.CourseHistoryCsv);
            Faculty = new FacultyStore(store.Get("faculty"), json => SaveSetting("faculty", json));
            Rules.Offline = OfflineRooms.FromJson(store.Get("offline"), json => SaveSetting("offline", json));
            _historyTurnedOff = new(JsonSerializer.Deserialize<List<string>>(store.Get("history-off") ?? "[]") ?? new(),
                                    StringComparer.OrdinalIgnoreCase);

            LoadRooms();
            ReloadHistory();
            LoadBuildingPriorities();
        }

        // ------------------------------------------------------------ stored files

        private record StoredFile(string Name, DateTime Saved);

        private List<StoredFile> Index() =>
            JsonSerializer.Deserialize<List<StoredFile>>(_store.Get("files") ?? "[]") ?? new();

        // Browser quota errors must not leave a replacement half-written or an index
        // pointing at deleted bytes. localStorage writes are atomic per key only.
        private void WriteStorage(params (string Key, string? Value)[] updates)
        {
            var before = updates.Select(u => (u.Key, Value: _store.Get(u.Key))).ToList();
            try
            {
                foreach (var update in updates)
                    if (update.Value == null) _store.Remove(update.Key); else _store.Set(update.Key, update.Value);
            }
            catch (Exception writeError)
            {
                var failures = new List<Exception>();
                foreach (var previous in before.AsEnumerable().Reverse())
                {
                    try { if (previous.Value == null) _store.Remove(previous.Key); else _store.Set(previous.Key, previous.Value); }
                    catch (Exception rollbackError) { failures.Add(rollbackError); }
                }
                if (failures.Count > 0)
                    throw new InvalidOperationException("Saving failed and the previous files could not be fully restored. Reload and check the file list.", new AggregateException(failures.Prepend(writeError)));
                throw;
            }
        }

        /// <summary>A saved file's bytes, for downloading it again.</summary>
        public byte[]? FileBytes(string name) =>
            _store.Get("file:" + name) is string b64 ? Convert.FromBase64String(b64) : null;

        private List<Xlsx.Sheet> Sheets(string name)
        {
            try
            {
                return FileBytes(name) is byte[] bytes ? Xlsx.Read(new MemoryStream(bytes)) : new();
            }
            catch (Exception)
            {
                return new();
            }
        }

        /// <summary>Every saved file, newest first, with what kind it is.</summary>
        public List<LibraryFile> Files() =>
            Index().OrderByDescending(f => f.Saved).Select(Describe).ToList();

        public List<LibraryFile> Of(FileKind kind) => Files().Where(f => f.Kind == kind).ToList();

        private LibraryFile Describe(StoredFile f)
        {
            if (_described.TryGetValue(f.Name, out var known) && known.Saved == f.Saved)
                return known;

            var bytes = FileBytes(f.Name) ?? Array.Empty<byte>();
            var info = Classify(f.Name, bytes, f.Saved, keepOther: _store.Get("other:" + f.Name) != null);
            _described[f.Name] = info;
            return info;
        }

        private void SaveSetting(string key, string value)
        {
            bool changed = _store.Get(key) != value;
            _store.Set(key, value);
            if (changed) MarkInputsChanged();
        }

        private void MarkInputsChanged()
        {
            _inputsRevision++;
            if (LatestPlan == null) return;
            _inputsChanged = true;
            Conversation.Sync(null);
        }

        private bool AffectsPlan(FileKind kind, string name) =>
            kind is FileKind.Rooms or FileKind.LectureCapture or FileKind.PastSchedule or FileKind.Reference or FileKind.Requests or FileKind.Preferences;

        /// <summary>
        /// What a file is, from its contents, not its name or where it was added.
        /// A spreadsheet the scheduler can't use, or any other kind of file, is
        /// Other when it was added under Additional files and Unknown otherwise.
        /// </summary>
        private LibraryFile Classify(string name, byte[] bytes, DateTime saved, bool keepOther)
        {
            LibraryFile Other(string why) => keepOther
                ? new LibraryFile(name, FileKind.Other, $"kept for reference ({SizeText(bytes.Length)})", saved)
                : new LibraryFile(name, FileKind.Unknown, why, saved);

            List<Xlsx.Sheet> sheets;
            try
            {
                sheets = Xlsx.Read(new MemoryStream(bytes));
            }
            catch (Exception)
            {
                return Other("not a spreadsheet (.xlsx)");
            }

            var past = PastSchedule.FromWorkbook(sheets);
            if (past.Sections.Count > 0)
                return new LibraryFile(name, FileKind.PastSchedule, $"{past.Sections.Count} sections, {past.Label}", saved);

            var workbook = DepartmentWorkbook.FromSheets(sheets);
            if (workbook.Sections.Count > 0)
                return new LibraryFile(name, FileKind.Requests,
                    $"{Fallback(workbook.Semester, "semester not named")}: {workbook.Sections.Count} sections, {workbook.Forms.Count} preference forms", saved);

            if (workbook.Forms.Count > 0)
                return new LibraryFile(name, FileKind.Preferences, $"{workbook.Forms.Count} preference forms", saved);
            if (ReferenceSheets.HasReferences(sheets))
                return new LibraryFile(name, FileKind.Reference, "course/faculty reference (documented template)", saved);

            var rooms = Room.FromFeatureWorkbook(sheets, _data.GdcRoomsCsv, Rules);
            if (rooms != null && rooms.Count > 0)
                return new LibraryFile(name, FileKind.Rooms, $"{rooms.Count(r => !r.DepartmentOnly)} rooms", saved);

            var capture = LectureCaptureNames(sheets);
            if (capture.Count > 0)
                return new LibraryFile(name, FileKind.LectureCapture, $"Lectures Online list, {capture.Count} rooms", saved);

            return Other("not a preference sheet, past schedule, rooms table or Lectures Online list");
        }

        /// <summary>
        /// Rooms in a Lectures Online list: a sheet with a Room column of names
        /// like "ART 1.102", and either a Normal Capacity column or "lecture" in the sheet name.
        /// </summary>
        public static List<string> LectureCaptureNames(List<Xlsx.Sheet> sheets)
        {
            foreach (var sheet in sheets)
            {
                int hr = sheet.Rows.FindIndex(r => r.Any(c => c.Trim().Equals("Room", StringComparison.OrdinalIgnoreCase)));
                if (hr < 0) continue;

                var h = sheet.Rows[hr].Select(c => c.Trim().ToLowerInvariant()).ToList();
                bool looksRight = h.Contains("normal capacity") || sheet.Name.Contains("lecture", StringComparison.OrdinalIgnoreCase);
                if (!looksRight) continue;

                int col = h.IndexOf("room");
                var names = sheet.Rows.Skip(hr + 1)
                    .Select(r => col < r.Count ? r[col].Trim() : "")
                    .Select(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length == 2 && p[0].All(char.IsLetter))
                    .Select(p => $"{p[0].ToUpperInvariant()} {RoomNumberText(p[1])}")
                    .Distinct()
                    .ToList();
                if (names.Count > 0) return names;
            }
            return new();
        }

        private static string RoomNumberText(string raw) =>
            raw.Contains('.') && double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
                : raw;

        /// <summary>
        /// Saves a file and puts it to use. <paramref name="section"/> is the step it
        /// was added under; the file's contents decide where it really goes, so a
        /// past schedule added as a preference sheet still becomes history.
        /// Under Additional files (<see cref="FileKind.Other"/>) anything is kept;
        /// elsewhere, a file the app can't use is turned away. Same name = replaced.
        /// </summary>
        public LibraryFile Save(string fileName, byte[] bytes, FileKind section)
        {
            string name = Path.GetFileName(fileName);
            var saved = DateTime.Now;
            var previous = Index().Select(f => f.Saved).DefaultIfEmpty(DateTime.MinValue).Max();
            if (saved <= previous) saved = previous.AddTicks(1);

            if (bytes.Length > MaxFileBytes)
                return new LibraryFile(name, FileKind.Unknown,
                    $"too large to keep in the browser ({SizeText(bytes.Length)}; the limit is {SizeText(MaxFileBytes)})", saved);

            var info = Classify(name, bytes, saved, keepOther: section == FileKind.Other);
            if (info.Kind == FileKind.Unknown) return info;

            var existing = Files().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.Kind != info.Kind)
                return new LibraryFile(name, FileKind.Unknown,
                    "that name is already used by a different type of file; rename this file to keep both", saved);
            if (existing != null) name = existing.Name;
            info = info with { Name = name };
            var index = Index().Where(f => f.Name != name).ToList();
            index.Add(new StoredFile(name, saved));
            var updates = new List<(string Key, string? Value)> {
                ("file:" + name, Convert.ToBase64String(bytes)),
                ("other:" + name, info.Kind == FileKind.Other ? "1" : null),
                ("files", JsonSerializer.Serialize(index))
            };
            if (info.Kind == FileKind.PastSchedule)
                updates.Add(("history-off", JsonSerializer.Serialize(_historyTurnedOff.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList())));
            WriteStorage(updates.ToArray());
            _described[name] = info;

            if (info.Kind == FileKind.PastSchedule)
            {
                _historyTurnedOff.Remove(name);
                ReloadHistory();
            }
            else if (info.Kind is FileKind.Rooms or FileKind.LectureCapture)
            {
                LoadRooms();
            }

            if (AffectsPlan(info.Kind, name)) MarkInputsChanged();

            return info;
        }

        public void Delete(string name)
        {
            var file = Files().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            name = file?.Name ?? name;
            var kind = file?.Kind;
            WriteStorage(("file:" + name, null), ("other:" + name, null),
                ("files", JsonSerializer.Serialize(Index().Where(f => f.Name != name).ToList())));
            _described.Remove(name);

            if (kind == FileKind.PastSchedule) ReloadHistory();
            if (kind is FileKind.Rooms or FileKind.LectureCapture) LoadRooms();
            if (kind != null && AffectsPlan(kind.Value, name)) MarkInputsChanged();
        }

        public void SetHistoryActive(string name, bool active)
        {
            var turnedOff = new HashSet<string>(_historyTurnedOff, StringComparer.OrdinalIgnoreCase);
            if (active) turnedOff.Remove(name);
            else turnedOff.Add(name);
            _store.Set("history-off", JsonSerializer.Serialize(turnedOff.ToList()));
            _historyTurnedOff = turnedOff;
            ReloadHistory();
            MarkInputsChanged();
        }

        /// <summary>History = every past-schedule file not turned off, newest file first.</summary>
        private void ReloadHistory()
        {
            ActiveHistory.Clear();
            var parts = new List<PastSchedule>();
            foreach (var f in Of(FileKind.PastSchedule).Where(f => !_historyTurnedOff.Contains(f.Name)))
            {
                parts.Add(PastSchedule.FromWorkbook(Sheets(f.Name)));
                ActiveHistory.Add(f.Name);
            }
            Past = PastSchedule.Combine(parts);
        }

        /// <summary>
        /// The newest rooms table added wins over the built-in one, and the newest
        /// Lectures Online list over the built-in list.
        /// </summary>
        private void LoadRooms()
        {
            var newestRooms = Of(FileKind.Rooms).FirstOrDefault();
            var fromFile = newestRooms == null ? null : Room.FromFeatureWorkbook(Sheets(newestRooms.Name), _data.GdcRoomsCsv, Rules);

            if (fromFile is { Count: > 0 })
            {
                Rooms = fromFile;
                _roomsTableSource = newestRooms!.Name;
            }
            else
            {
                Rooms = Room.FromCsvText(_data.RoomsCsv, _data.GdcRoomsCsv, Rules);
                _roomsTableSource = "the built-in rooms table";
            }

            var newestList = Of(FileKind.LectureCapture).FirstOrDefault();
            HashSet<string> names;
            if (newestList != null)
            {
                names = LectureCaptureNames(Sheets(newestList.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var r in Rooms) r.LectureCapture = names.Contains(r.DisplayName);
                LectureCaptureRooms = Rooms.Count(r => r.LectureCapture);
                LectureCaptureSource = newestList.Name;
            }
            else
            {
                Room.MarkLectureCaptureText(Rooms, _data.LectureCaptureCsv);
                names = Csv.Lines(_data.LectureCaptureCsv).Skip(1).Select(Csv.ParseLine).Where(r => r.Count >= 2)
                    .Select(r => $"{r[0].Trim()} {r[1].Trim()}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                LectureCaptureSource = "the built-in Lectures Online list";
            }
            LoadAddedRooms(names);
            LectureCaptureRooms = Rooms.Count(r => r.LectureCapture);
        }

        // ------------------------------------------------------------ planning

        public Planner.Plan Build(string fileName, string? preferencesFile = null)
        {
            if (Rooms.Count == 0)
                throw new InvalidDataException("The room inventory is empty. Reload the app or upload a rooms table before building a schedule.");
            var workbook = DepartmentWorkbook.FromSheets(Sheets(fileName));
            if (workbook.Sections.Count == 0)
                throw new InvalidDataException(
                    "No sections found. The workbook needs a sheet with a Title column, like the department's Classes App sheet.");

            if (!string.IsNullOrEmpty(preferencesFile))
            {
                var preferences = DepartmentWorkbook.FromSheets(Sheets(preferencesFile)).Forms;
                if (preferences.Count == 0)
                    throw new InvalidDataException($"No scheduling preference forms found in '{preferencesFile}'. Select an existing preferences file or use the tabs in the Classes App workbook.");
                workbook.Forms.Clear();
                workbook.Forms.AddRange(preferences);
            }
            // Stage profile imports so invalid updates or failed storage writes cannot
            // partially replace the profiles used by the previous schedule.
            var buildFaculty = new FacultyStore(Faculty.Snapshot(), json => SaveSetting("faculty", json));
            foreach (var profile in buildFaculty.All) profile.PrepareForImport();
            var referenceFiles = Files().Where(f => f.Kind == FileKind.Reference ||
                f.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
                f.Name.Equals(preferencesFile, StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Saved);
            foreach (var file in referenceFiles)
            {
                if (file.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    foreach (var section in workbook.Sections.Where(s => !s.InstructorMissing && !buildFaculty.IsRemoved(s.Surname)))
                    {
                        var profile = buildFaculty.GetOrAdd(section.Surname, section.Instructor);
                        if (!profile.TrackExplicitlySet && section.Track != FacultyTrack.Unknown &&
                            (profile.ImportedTrack.HasValue || profile.Track == FacultyTrack.Unknown))
                        { profile.Track = section.Track; profile.ImportedTrack = section.Track; }
                        if (!profile.DifficultExplicitlySet)
                        { profile.Difficult = section.Difficult; profile.ImportedDifficult = section.Difficult; }
                    }
                ReferenceSheets.Apply(Sheets(file.Name), workbook, buildFaculty);
            }
            foreach (var section in workbook.Sections)
            {
                var value = _store.Get(BaselineKey(workbook.Semester, section.Course));
                if (value == null) continue;
                var baseline = JsonSerializer.Deserialize<int[]>(value)!;
                section.MinimumRoomCapacity = baseline[0];
                section.PreferredRoomCapacity = baseline[1];
            }
            var planner = new Planner(Rooms, Campus, Past, buildFaculty, Rules, DepartmentHistory);
            var plan = planner.Build(workbook);
            buildFaculty.Save();
            Faculty = buildFaculty;
            _pendingFileConfirmation = null;
            _pendingBuildingPriority = null;
            _pendingCourseBaseline = null;
            _planner = planner;
            LatestPlan = plan;
            LatestPreferences = preferencesFile;
            Conversation.Sync(LatestPlan);
            LatestSource = fileName;
            _inputsChanged = false;
            _builtRules = JsonSerializer.Serialize(Rules);
            return LatestPlan;
        }

        private Planner CurrentPlanner()
        {
            if (_planner == null) throw new InvalidOperationException("Build a schedule first.");
            if (RequiresRebuild) throw new InvalidOperationException("Planning inputs changed. Rebuild the schedule before previewing or applying changes.");
            return _planner;
        }

        public Planner.OverrideProposal ProposeOverride(string request, bool allowOversizedRoom) =>
            CurrentPlanner().ProposeOverride(LatestPlan!, request, allowOversizedRoom);
        public Planner.OverrideProposal ProposeMove(Placement target, Room room, MeetingTime time, bool allowOversizedRoom) =>
            CurrentPlanner().ProposeOverride(LatestPlan!, target, room, time, allowOversizedRoom);
        public Planner.OverrideProposal ProposeSwap(Placement first, Placement second, bool swapTimes, bool allowOversizedRoom) =>
            CurrentPlanner().ProposeSwap(LatestPlan!, first, second, swapTimes, allowOversizedRoom);
        public void ApplyOverride(Planner.OverrideProposal proposal) =>
            CurrentPlanner().ApplyOverride(LatestPlan!, proposal);

        public string? LatestPreferences { get; private set; }
        private static string BaselineKey(string semester, string course) => "baseline:" + semester + ":" + course;
        public void SetSemesterBaseline(string semester, string course, int minimum, int preferred)
        {
            if (minimum < 0 || preferred < 0 || (preferred > 0 && preferred < minimum))
                throw new ArgumentException("Capacities must be nonnegative; preferred must be at least minimum.");
            SaveSetting(BaselineKey(semester, course), JsonSerializer.Serialize(new[] { minimum, preferred }));
        }

        public static string SizeText(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

        private static string Fallback(string s, string f) => string.IsNullOrWhiteSpace(s) ? f : s;
    }
}
