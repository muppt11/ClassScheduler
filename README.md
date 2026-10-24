# RoomScheduler

Builds the UT Austin CS department's semester schedule from the department's own
spreadsheets. It picks a time and a room for every section, follows the department's
rules, fills in the blank fields on the Classes App sheet, and lists what a person should
check. There's also a console version.

## Open it

**https://muppt11.github.io/ClassScheduler/**

The whole app runs inside your browser. Spreadsheets you add are read in the browser and
saved in its local storage, so they stay on your computer and are never uploaded anywhere.
They're still there next time you open the page in the same browser. Each push to `main`
republishes the site (`.github/workflows/pages.yml`).

To run it locally instead, install the [.NET 10 SDK](https://dotnet.microsoft.com/download),
run `dotnet run --project src/RoomScheduler.Web`, and open the address it prints.

## Each semester

1. **Semester plan: add files in four steps.**
   1. Classes and preferences (required): either a workbook with Classes App and Scheduling Preferences tabs,
      or two separate files. Select the Classes App workbook, then explicitly select the separate preferences file.
      Same-name files of different types are rejected; rename to keep both. Same-name files of the same type replace their prior version.
   2. Past schedules (recommended): each finished semester's registrar export (CNBR, DAYS1,
      BLDG1...). The newest counts as "last time".
   3. Rooms table (optional): only if the registrar sends a new one.
   4. Additional files (optional): a new Lectures Online room list is used for recording
      requests; anything else (notes, maps) is kept for reference and can be downloaded again.

   The app checks each file's contents, so a file added under the wrong step still goes to
   the right one. Files stay in this browser for next time.
2. **Rooms → Offline.** Check any building or room that's off limits. CPE starts checked.
3. **Faculty.** The track list sets each professor to tenure (T) or professional (P) and
   marks difficult faculty (D). Each profile also holds times someone can't teach,
   accommodations and complaints. On the schedule, the **D** and **T**/**P** buttons do the same.
4. **Build schedule.** Then work through the **Review queue** (problems first).
   **Rebuild** after changing faculty or offline rooms.
5. **Download workbook.** It contains:
   - the Classes App sheet with blanks filled in (highlighted), plus the registrar cap and fit
   - the review queue
   - a registrar sheet
   - finals and proctors (exam times and rooms are TBD)
   - a weekly calendar for every room in use plus the GDC rooms, in the Gates template
     layout. Used rooms come first, marked ● with a green tab, after an index sheet

## The rules

**Order.** Difficult faculty first, then tenure track, then professional track. Track comes
from the "Tenure or PTF" column, or the Faculty page if set there.

**Times.**
- Each section tries the professor's ranked choices.
- The notes ("Time other") are read too. When they say the choices were placeholders, or
  all three choices are the same, the notes come first.
- "Friday any time between 9am-4pm" becomes every 3-hour slot in that window.
- No form: the time they taught that course last time.
- A new instructor with no form and no history receives a provisional standard time and feasible room,
  subject to faculty availability and hard policies, and is flagged for confirmation. Missing size uses a
  course baseline or a configurable provisional size of 25; it never defaults to zero.

**Never broken.**
- Tenure-track faculty get nothing overlapping Mon or Wed 12-1 (faculty lunch), even if asked.
- TTH 11-12:30 is held for graduate courses (middle digit 8 or 9, like 381B or 395T). A
  tenure-track professor who teaches one can't have their other classes then.
- No class goes in an offline building or room.
- Times a professor wrote they can't do ("not Wed noon-1PM") and anything on their
  profile (unavailable times, accommodations) are respected.
- No room or instructor is double booked.

**Rooms.** Points go up for:
- rooms, buildings or floors they asked for
- the room they had last time
- buildings CS used in the 2022-2026 catalogs
- rooms on the Lectures Online list, when they asked for recording
- being close to GDC (strongly when they asked to be near it)

Points go down for:
- buildings they asked to avoid
- missing requested features
- non-standard classrooms (NSC)
- long walks between a professor's back-to-back classes: under 5 minutes is ideal, under
  10 is fine, and longer is flagged

Before 12:30, CS's four big GDC rooms need the registrar's agreement, so those bookings are marked.

**Class size.**
- The size reported to the registrar is room capacity minus 10%. It's kept close to the
  expected class size.
- Normal is under 50% over; Min high is 50-75% over; Max high is over 75%, and is flagged.
  A room more than 10% too small is also flagged.
- A class whose historical enrollment is well under its projected size is downsized 15% and flagged.
  Historical enrollment is the median of per-semester mean enrollments across active uploads.
  Matching instructor history is used when it spans at least two terms; otherwise matching course/title
  history is used. Zero/missing enrollments are excluded. A single available term is still uncertain.
- A final recovery pass tries to rearrange occupied rooms at the same time to place stranded sections.
  It then tries provisional alternate times for sections still unassigned, subject to the same room,
  instructor, availability, tenure-lunch and accommodation constraints. These time changes are marked
  for review. Uncheck “Try provisional alternate times” before building to keep the requested times only.
  Truly impossible capacity/availability cases retain actionable review explanations. This is a bounded
  heuristic (room chains up to four moves and 5,000 room attempts per stranded section), not a proof
  of maximum possible utilization.

**Flagged as possible inaccuracies.**
- A time that contradicts the professor's own note.
- Hours that don't match the course's credit hours (439 is 4 credits, so 4 hours a week).
- "Same as last year" when the record says otherwise.
- A room that doesn't exist.
- A graduate course with a professional-track instructor.

Cross-listing, textbook requests and other free-text notes go to the review queue.

## Optional reference templates and semester baselines

These are **new, explicit templates**, not inferred columns in the existing department files.
Add them under Additional files, or as tabs in the selected Classes App workbook. Headers belong
in the first row; blanks/0 mean no capacity baseline. CAP1 and CAP2 in registrar exports describe
historical room capacities and are **not** treated as target enrollment or baseline values.

| Sheet name | Required headers | Optional headers |
|---|---|---|
| Course Reference | Course, plus Minimum Room Capacity and/or Preferred Room Capacity | Semester |
| Faculty Reference | Professor, Tenure or PTF | |

For example, a Course Reference row can be `314 | 100 | 150 | Fall 2027`, with those columns
in the order Course, Minimum Room Capacity, Preferred Room Capacity, Semester. Course uses the
same number as Classes App (e.g. 314, 398T); a topics-course baseline applies to every section
with that number. Semester is the Classes App label, e.g. `Fall 2027`; blank applies to all terms.
Use separate rows or files for semester exceptions; later matching rows/files override earlier
ones. Reference files are read oldest first, then tabs in the selected Classes App workbook.
The Semester plan baseline editor takes precedence over both and persists by semester/course.
It displays the current minimum/preferred values and rebuilds the plan when saved.

Minimum Room Capacity is a **hard physical-seat floor**, even after historical downsizing.
Preferred Room Capacity is a **scoring preference**, so other preferences and availability may
outweigh it. Neither value overwrites known expected enrollment. Rooms must hold at least the
expected enrollment and minimum physical capacity; the existing registrar cap remains physical
capacity minus 10%, with tight/oversized-room review flags preserved. No extra 10% is added to
the minimum or preferred capacity. When enrollment is entirely missing, the baseline's capacity
minus 10% supplies a provisional expected size; without a baseline it is 25, flagged for review.
The existing Classes App “Minimum Room Size Needed” remains the projected enrollment input.
There is no separately verified current-enrollment column in these request samples.

Faculty Reference accepts T and P/PTF. It fills unknown profile tracks; existing saved profiles
and edits in Faculty or the schedule take precedence. Remove a reference file to stop future
imports; already saved faculty profiles remain editable and reusable. Matching still uses the
existing surname key, so two instructors with the same surname need manual verification.
Missing tracks produce one informational plan note, rather than flagging every section for review.
Unknown tracks retain lowest priority; tenure-specific constraints require a known track.

Rooms now shows exact room usage from active registrar uploads, with course/semester details on
hover, including hybrid courses. The local Fall 2025 sample has three hybrid CS 303E rows in
**ART 1.102**, not ART 1.0. Previously the Rooms page displayed catalog **building** counts for
every room in that building; it did not expose this exact-room registrar history. Room numbers
are normalized from Excel numeric noise. Catalog building history still informs room scoring.

## Manual override preview

Use **Chatbot** in the navigation or the persistent bottom-right **Chatbot** button on any page.
The same **Scheduling chatbot** is also at the top of Semester plan. It is visible
even before files are uploaded. Type `Help` for upload and command guidance, or `Show review summary`
for a summary of the built plan, or `Which classes have no room?` for specific explanations. This is a local command assistant, not a connected general AI chatbot.
For a manual override, enter, for example:

```
Put 398T in GDC 2.216 Monday through Thursday at 10:00am-11:00am
```

Use a course number or exact title that identifies one section. If several sections share it,
use `title / instructor / row N`, where N is the original spreadsheet row. Use explicit am/pm;
a start time alone retains the section's existing per-meeting duration. Preview lists every
changed room/time before Apply. Cancel or a validation error leaves assignments unchanged.

The preview validates physical capacity, offline rooms, faculty/profile and preference-note
unavailability, tenure lunch/graduate-slot rules, accommodations, and room/instructor overlaps.
It searches replacement rooms for directly displaced sections without changing their times.
Apply rechecks the entire proposed schedule and rejects stale previews. No external availability
feed exists: availability here means the uploaded/planned schedule and configured offline rooms.
Weekly-hour exceptions and registrar permission before CS-controlled GDC hours still require review.
A request for a substantially oversized room requires the explicit per-request exception checkbox;
this can accommodate Computer Architecture or 398T in GDC 2.216 without hard-coded course rules.
Approved moves are marked for review and appear in exports. Rebuild/page reload replaces them.

## Known gaps

- **Discussion sections** are phase two, after the lecture schedule is approved. The code is
  there and switched off (`ScheduleDiscussions` in `SchedulingRules.cs`).
- **Final exams** are TBD.
- **Only Fall 2025 actuals ship in the local sample set.** Add older semesters through Files to improve
  enrollment estimates; latest-term time/room continuity remains separate from enrollment statistics.
- **Manual overrides use a constrained grammar.** They can rehouse directly displaced classes at their
  existing times. They do not search a chain of time changes or move other room occupants to free up
  replacement rooms. Infeasible requests are rejected without changing the plan. Overrides last until
  rebuild or page reload; download the workbook to preserve an approved result.
- **Building distances are estimates**, from the registrar's GPC map at roughly 1.9 m per
  pixel (`data/buildings.csv`). EER isn't on that map; its position comes from its label.
- **One browser.** Files, profiles and offline rooms are saved in the browser you use. A
  different computer or browser starts empty, and clearing site data erases them.
- **Speed.** Building runs in the browser and takes a few seconds or more.

## Where things live

| Path | What |
|---|---|
| `data/private/` | department spreadsheets for the console version (gitignored) |
| `data/gpc_rooms_features.csv` | built-in rooms table |
| `data/gdc_rooms.csv` | GDC rooms from the Gates template |
| `data/lecture_capture_rooms.csv` | Lectures Online rooms |
| `data/buildings.csv` | building positions for walking distances |
| `data/course_history.csv` | buildings each department used, 2022-2026 catalogs |
| `src/RoomScheduler.Core/Planning/SchedulingRules.cs` | thresholds and policies in one place |

**Privacy.** The department sheets have EIDs, emails and private comments, and this repo
and its website are public. The website only reads them inside the browser, and the
console version reads them from `data/private/`, which is never committed.

## Console version and testing

`dotnet test` runs the tests for the file steps and the planner. If the department's files
are in `data/private/`, it also checks them: they're recognized, they build a full schedule,
and there are no double bookings or classes in CPE. GitHub runs the tests before every deploy.

```
dotnet run --project src/RoomScheduler.Cli data/private/fall26_requests.xlsx \
    --history data/private/fall25_actual.xlsx --out fall26.xlsx --list --flags
```

`--compare actual.xlsx` scores a plan against what really ran. On the Fall 2025 requests
(with no history, since there is no Fall 2024 file), it picks the same time as the real
schedule for 67% of sections and the same building for 51%. Most misses are places where
the request was changed by hand. Every run also checks itself for double bookings and
broken lunch rules.

## What I learned

**Check the data before trusting it.** The registrar file lists SZB 1.510 twice. I
loaded it as is, so the app treated one room as two and booked it twice at the same
time. I only caught it because I wrote a script that reads the output back and looks
for overlaps.

**One day per class is not how school works.** Classes meet MWF or TTH, and an MWF
class ties up its room three days a week. My first version used a single day and made
buildings look much emptier than they are.

**The old schedules are photos, not text.** I had to OCR them first, and even then the words come out as scattered fragments with coordinates instead of lines. I had to
rebuild each row from the PDF text positions before I could parse it.

**I picked the wrong signal first.** I assumed I could match a class to the exact room it used before. Departments mostly teach in their own buildings, and those rooms are
not general purpose classrooms, so only 19 of the rooms in my history can even be
scheduled. Matching on the building works because there is far more of it and OCR
mistakes matter less.

**Same algorithms don't always work.** Turning the building preference on pushed unplaced
courses from 77 to 90 out of 600, because everything crowds into the same few
buildings. I kept it because the picks look more realistic.

## Layout

```
src/RoomScheduler.Core     scheduling logic, no UI
  Planning/                planner, preference parsing, rules, exports, faculty profiles,
                           offline rooms, past schedules, campus distances
  Xlsx.cs                  reads and writes .xlsx without a NuGet package
  Room.cs, CourseHistory.cs, Csv.cs, DataFile.cs
src/RoomScheduler.Web      the website (Blazor WebAssembly): Semester plan, Faculty, Rooms
src/RoomScheduler.Cli      console version
tools/, class schedule resources/   how course_history.csv was made from scanned catalogs
```
