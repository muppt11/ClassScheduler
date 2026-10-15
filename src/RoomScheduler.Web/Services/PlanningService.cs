using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace RoomScheduler.Web.Services
{
    /// <summary>What kind of department spreadsheet a file is, judged from its columns.</summary>
    public enum FileKind
    {
        /// <summary>A semester's Classes App sheet plus preference forms: what to schedule.</summary>
        Requests,

        /// <summary>A registrar export of what actually ran (CNBR, DAYS1, BLDG1...): history.</summary>
        PastSchedule,

        /// <summary>The GPC rooms-with-features table.</summary>
        Rooms,

        Unknown
    }

    public record LibraryFile(string Name, FileKind Kind, string Detail, DateTime Saved);

    /// <summary>
    /// Everything the planner needs, kept in the browser. The public data files
    /// (rooms, buildings, history) are downloaded with the app. The department's
    /// spreadsheets, faculty profiles and offline rooms are saved in this
    /// browser's local storage, so they stay on this computer and are there on
    /// the next visit. Nothing is uploaded anywhere.
    /// </summary>
    public class PlanningService
    {
        public SchedulingRules Rules { get; } = new();
        public List<Room> Rooms { get; private set; } = new();
        public string RoomsSource { get; private set; } = "the built-in rooms table";
        public Campus Campus { get; private set; } = new();
        public FacultyStore Faculty { get; private set; } = FacultyStore.InMemory();
        public CourseHistory DepartmentHistory { get; private set; } = CourseHistory.Empty;
        public int LectureCaptureRooms { get; private set; }

        public PastSchedule Past { get; private set; } = PastSchedule.Empty;

        /// <summary>Past-schedule files currently used as history. All of them unless turned off.</summary>
        public HashSet<string> ActiveHistory { get; } = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _historyTurnedOff = new(StringComparer.OrdinalIgnoreCase);

        public Planner.Plan? LatestPlan { get; private set; }
        public string LatestSource { get; private set; } = "";

        private const string Prefix = "roomscheduler:";
        private readonly IJSInProcessRuntime _js;
        private string _builtInRooms = "", _gdcRooms = "", _captureList = "";
        private readonly Dictionary<string, LibraryFile> _described = new(StringComparer.OrdinalIgnoreCase);

        public PlanningService(IJSRuntime js)
        {
            _js = (IJSInProcessRuntime)js;
        }

        /// <summary>Downloads the public data files and reads what this browser has saved.</summary>
        public async Task InitAsync(HttpClient http)
        {
            async Task<string> Fetch(string name)
            {
                try { return await http.GetStringAsync("data/" + name); }
                catch (HttpRequestException) { return ""; }
            }

            _builtInRooms = await Fetch("gpc_rooms_features.csv");
            _gdcRooms = await Fetch("gdc_rooms.csv");
            _captureList = await Fetch("lecture_capture_rooms.csv");
            Campus = Campus.FromCsvText(await Fetch("buildings.csv"));
            DepartmentHistory = CourseHistory.FromCsvText(await Fetch("course_history.csv"));

            Faculty = new FacultyStore(Get("faculty"), json => Set("faculty", json));
            Rules.Offline = OfflineRooms.FromJson(Get("offline"), json => Set("offline", json));
            _historyTurnedOff = new(JsonSerializer.Deserialize<List<string>>(Get("history-off") ?? "[]") ?? new(),
                                    StringComparer.OrdinalIgnoreCase);

            LoadNewestRooms();
            ReloadHistory();
        }

        // ------------------------------------------------------------ browser storage

        private string? Get(string key) => _js.Invoke<string?>("localStorage.getItem", Prefix + key);

        private void Set(string key, string value)
        {
            try
            {
                _js.InvokeVoid("localStorage.setItem", Prefix + key, value);
            }
            catch (JSException e)
            {
                throw new InvalidOperationException(
                    "The browser's storage is full. Remove some files under Files and try again.", e);
            }
        }

        private void Remove(string key) => _js.InvokeVoid("localStorage.removeItem", Prefix + key);

        private record StoredFile(string Name, DateTime Saved);

        private List<StoredFile> Index() =>
            JsonSerializer.Deserialize<List<StoredFile>>(Get("files") ?? "[]") ?? new();

        private void SaveIndex(List<StoredFile> index) => Set("files", JsonSerializer.Serialize(index));

        private byte[]? Bytes(string name) =>
            Get("file:" + name) is string b64 ? Convert.FromBase64String(b64) : null;

        private List<Xlsx.Sheet> Sheets(string name) =>
            Bytes(name) is byte[] bytes ? Xlsx.Read(new MemoryStream(bytes)) : new();

        // ------------------------------------------------------------ library

        /// <summary>Every spreadsheet saved in this browser, newest first, with what kind it is.</summary>
        public List<LibraryFile> Library() =>
            Index().OrderByDescending(f => f.Saved).Select(Describe).ToList();

        public List<LibraryFile> Of(FileKind kind) => Library().Where(f => f.Kind == kind).ToList();

        private LibraryFile Describe(StoredFile f)
        {
            if (_described.TryGetValue(f.Name, out var known) && known.Saved == f.Saved)
                return known;

            LibraryFile info;
            try
            {
                info = Classify(f.Name, Sheets(f.Name), f.Saved);
            }
            catch (Exception)
            {
                info = new LibraryFile(f.Name, FileKind.Unknown, "could not be read as a spreadsheet", f.Saved);
            }

            _described[f.Name] = info;
            return info;
        }

        private LibraryFile Classify(string name, List<Xlsx.Sheet> sheets, DateTime saved)
        {
            var past = PastSchedule.FromWorkbook(sheets);
            if (past.Sections.Count > 0)
                return new LibraryFile(name, FileKind.PastSchedule, $"{past.Sections.Count} sections, {past.Label}", saved);

            var workbook = DepartmentWorkbook.FromSheets(sheets);
            if (workbook.Sections.Count > 0)
                return new LibraryFile(name, FileKind.Requests,
                    $"{Fallback(workbook.Semester, "semester not named")}: {workbook.Sections.Count} sections, {workbook.Forms.Count} preference forms", saved);

            var rooms = Room.FromFeatureWorkbook(sheets, _gdcRooms, Rules);
            if (rooms != null && rooms.Count > 0)
                return new LibraryFile(name, FileKind.Rooms, $"{rooms.Count(r => !r.DepartmentOnly)} rooms", saved);

            return new LibraryFile(name, FileKind.Unknown,
                "not recognized: needs a Classes App sheet (Title column), a registrar export (CNBR column) or a rooms table (Features column)", saved);
        }

        /// <summary>
        /// Saves a spreadsheet in this browser and puts it to use: a past schedule
        /// joins the history, a rooms table replaces the room list. A file with the
        /// same name is replaced. Files that aren't recognized are not kept.
        /// </summary>
        public LibraryFile Save(string fileName, byte[] bytes)
        {
            string name = Path.GetFileName(fileName);
            var saved = DateTime.Now;

            List<Xlsx.Sheet> sheets;
            try
            {
                sheets = Xlsx.Read(new MemoryStream(bytes));
            }
            catch (Exception)
            {
                return new LibraryFile(name, FileKind.Unknown, "could not be read as a spreadsheet", saved);
            }

            var info = Classify(name, sheets, saved);
            if (info.Kind == FileKind.Unknown) return info;

            Set("file:" + name, Convert.ToBase64String(bytes));
            var index = Index().Where(f => f.Name != name).ToList();
            index.Add(new StoredFile(name, saved));
            SaveIndex(index);
            _described[name] = info;

            if (info.Kind == FileKind.PastSchedule)
            {
                _historyTurnedOff.Remove(name);
                SaveHistoryChoice();
                ReloadHistory();
            }
            else if (info.Kind == FileKind.Rooms)
            {
                LoadNewestRooms();
            }

            return info;
        }

        public void Delete(string name)
        {
            var kind = Library().FirstOrDefault(f => f.Name == name)?.Kind;
            Remove("file:" + name);
            SaveIndex(Index().Where(f => f.Name != name).ToList());
            _described.Remove(name);

            if (kind == FileKind.PastSchedule) ReloadHistory();
            if (kind == FileKind.Rooms) LoadNewestRooms();
        }

        public void SetHistoryActive(string name, bool active)
        {
            if (active) _historyTurnedOff.Remove(name);
            else _historyTurnedOff.Add(name);
            SaveHistoryChoice();
            ReloadHistory();
        }

        private void SaveHistoryChoice() =>
            Set("history-off", JsonSerializer.Serialize(_historyTurnedOff.ToList()));

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

        /// <summary>The newest rooms table saved here wins over the built-in one.</summary>
        private void LoadNewestRooms()
        {
            var newest = Of(FileKind.Rooms).FirstOrDefault();
            var fromFile = newest == null ? null : Room.FromFeatureWorkbook(Sheets(newest.Name), _gdcRooms, Rules);

            if (fromFile is { Count: > 0 })
            {
                Rooms = fromFile;
                RoomsSource = newest!.Name;
            }
            else
            {
                Rooms = Room.FromCsvText(_builtInRooms, _gdcRooms, Rules);
                RoomsSource = "the built-in rooms table";
            }

            LectureCaptureRooms = Room.MarkLectureCaptureText(Rooms, _captureList);
        }

        // ------------------------------------------------------------ planning

        public Planner.Plan Build(string fileName)
        {
            var workbook = DepartmentWorkbook.FromSheets(Sheets(fileName));
            if (workbook.Sections.Count == 0)
                throw new InvalidDataException(
                    "No sections found. The workbook needs a sheet with a Title column, like the department's Classes App sheet.");

            LatestPlan = new Planner(Rooms, Campus, Past, Faculty, Rules, DepartmentHistory).Build(workbook);
            LatestSource = fileName;
            Faculty.Save();
            return LatestPlan;
        }

        private static string Fallback(string s, string f) => string.IsNullOrWhiteSpace(s) ? f : s;
    }
}
