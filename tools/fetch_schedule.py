"""
Saves UT Austin course schedule pages for later parsing.

You log in yourself, in a real Chrome window, with your own UT EID and Duo push.
This script never sees, asks for, or stores your password. It only saves the
HTML of pages you have already navigated to, and clicks through the "next page"
links so you do not have to.

Setup, once:
    py -m pip install playwright

Run:
    py tools/fetch_schedule.py

The browser profile is kept in tools/.browser_profile so you usually stay
logged in between runs. That folder is git-ignored.
"""

import sys
import time
from pathlib import Path

try:
    from playwright.sync_api import sync_playwright
except ImportError:
    sys.exit(
        "Playwright is not installed.\n"
        "Run:  py -m pip install playwright\n"
    )

# Semester codes: year followed by 2 for spring, 6 for summer, 9 for fall.
# 20242 is Spring 2024. 20262 is Spring 2026.
SEMESTER = "20262"

START_URL = f"https://utdirect.utexas.edu/apps/registrar/course_schedule/{SEMESTER}/"

OUT_DIR = Path(__file__).parent / "saved_pages"
PROFILE_DIR = Path(__file__).parent / ".browser_profile"

# Be a considerate guest on someone else's server.
DELAY_SECONDS = 1.5

# Link text used for pagination on the results pages.
NEXT_LINK_PATTERNS = ["Next Page", "next page", "Next", "next"]


def save_page(page, label, index):
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    safe = "".join(c if c.isalnum() or c in "-_" else "_" for c in label)
    path = OUT_DIR / f"{SEMESTER}_{safe}_{index:02d}.html"
    path.write_text(page.content(), encoding="utf-8")
    print(f"    saved {path.name}  ({len(page.content()):,} bytes)")
    return path


def find_next_link(page):
    """Returns a clickable 'next page' link, or None if this is the last page."""
    for pattern in NEXT_LINK_PATTERNS:
        link = page.get_by_role("link", name=pattern, exact=False)
        try:
            if link.count() > 0 and link.first.is_visible():
                return link.first
        except Exception:
            continue
    return None


def capture_current_listing(page, label):
    """Saves the current page, then follows pagination to the end."""
    index = 1
    save_page(page, label, index)

    while True:
        next_link = find_next_link(page)
        if next_link is None:
            print("    no further pages")
            break

        try:
            next_link.click()
            page.wait_for_load_state("networkidle")
        except Exception as exc:
            print(f"    stopped paginating: {exc}")
            break

        index += 1
        time.sleep(DELAY_SECONDS)
        save_page(page, label, index)

        if index > 100:
            print("    stopping at 100 pages as a safety limit")
            break

    return index


def main():
    print(__doc__)
    print(f"Semester code: {SEMESTER}")
    print(f"Saving pages to: {OUT_DIR}\n")

    with sync_playwright() as p:
        context = p.chromium.launch_persistent_context(
            user_data_dir=str(PROFILE_DIR),
            headless=False,
            channel="chrome",
            args=["--start-maximized"],
            no_viewport=True,
        )

        page = context.pages[0] if context.pages else context.new_page()
        page.goto(START_URL)

        print("A Chrome window is open.")
        print("1. Log in with your UT EID and approve the Duo prompt.")
        print("2. Navigate to a department's course listing.")
        print("3. Come back here and enter a short label, such as ARC.")
        print("   The script saves that listing and every following page.")
        print("4. Repeat for as many departments as you want.")
        print("   Enter q when you are finished.\n")

        total_pages = 0
        while True:
            label = input("Label for the listing now on screen (or q to quit): ").strip()
            if label.lower() in {"q", "quit", "exit"}:
                break
            if not label:
                print("  Please enter a label, such as ARC or CS.")
                continue

            print(f"  Capturing '{label}' starting at:\n    {page.url}")
            pages_saved = capture_current_listing(page, label)
            total_pages += pages_saved
            print(f"  Done. {pages_saved} page(s) saved for {label}.\n")

        context.close()

    print(f"\nFinished. {total_pages} page(s) saved to {OUT_DIR}")
    if total_pages:
        print("Next step: hand these files over so they can be parsed into a CSV.")


if __name__ == "__main__":
    main()
