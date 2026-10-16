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

1. **Semester plan → Files → Add files.** Upload the new Classes App + preference workbook.
   Also add each finished semester's actual schedule (the registrar export with CNBR,
   DAYS1, BLDG1...) and a new rooms table if the registrar sends one. The app works out
   which file is which and keeps them in this browser for next time. All past schedules are used as
   history; the newest counts as "last time".
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
- A new instructor with no form and no history is flagged for someone to reach out.

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
- A class whose last enrollment was well under its projected size is downsized 15% and flagged.
- After everything is placed, a clean-up pass moves sections into better rooms that turned out to be free.

**Flagged as possible inaccuracies.**
- A time that contradicts the professor's own note.
- Hours that don't match the course's credit hours (439 is 4 credits, so 4 hours a week).
- "Same as last year" when the record says otherwise.
- A room that doesn't exist.
- A graduate course with a professional-track instructor.

Cross-listing, textbook requests and other free-text notes go to the review queue.

## Known gaps

- **Discussion sections** are phase two, after the lecture schedule is approved. The code is
  there and switched off (`ScheduleDiscussions` in `SchedulingRules.cs`).
- **Final exams** are TBD.
- **Only Fall 2025 history so far.** Add older semesters through Files to improve the
  "last time" fallback and the checks.
- **No moving a single class by hand in the app.** Change it in the downloaded workbook,
  or change the request and rebuild.
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
