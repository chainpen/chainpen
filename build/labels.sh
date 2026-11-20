#!/usr/bin/env bash
# Creates or updates the repository's issue labels (FS-901). The issue forms in .github/ISSUE_TEMPLATE apply the
# first group automatically, but only if the labels exist, so run this once when the repository goes public and
# again after changing the list. Needs the GitHub CLI signed in with access to the repository.
#   build/labels.sh [owner/repo]
set -euo pipefail
repo="${1:-fillsign/fillsign}"

label() { gh label create "$1" --repo "$repo" --color "$2" --description "$3" --force; }

# What kind of issue: set by the issue form.
label bug          d73a4a "Something doesn't work as it should"
label pdf          e99695 "A particular PDF doesn't open, show or fill correctly"
label idea         a2eeef "A feature or improvement to consider"
label crash        b60205 "Fillsign stopped unexpectedly (from a crash report)"
label needs-triage fbca04 "Not looked at yet"

# Where: added during triage.
label windows       1d76db "Happens on Windows"
label macos         1d76db "Happens on macOS"
label linux         1d76db "Happens on Linux"
label accessibility 5319e7 "Screen readers, keyboard use, contrast or zoom"

# What happens next: added during triage.
label planned      0e8a16 "On the board (docs/KANBAN.md) to be done"
label needs-info   d4c5f9 "Waiting for more detail from whoever reported it"
label duplicate    cfd3d7 "Already reported in another issue"
label not-planned  ffffff "Considered, and not something Fillsign will do"

# GitHub's defaults this list replaces.
for unused in documentation enhancement "good first issue" "help wanted" invalid question wontfix; do
  gh label delete "$unused" --repo "$repo" --yes 2>/dev/null || true
done
