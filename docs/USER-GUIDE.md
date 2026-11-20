# Fillsign user guide

Fillsign fills in and signs documents, especially scans and photos of paper forms that have no fillable fields. Everything stays on your computer: there's no account and nothing is uploaded.

## Open a document

- **Open…** (⌘O / Ctrl+O), drop files on the window, or pick one from **Recent** on the start screen.
- PDFs, JPGs, PNGs and other pictures all work. Several files open as one document, in the order you chose.
- If a document is already open, Fillsign asks whether to add the pages to it or open them in a new window. It never replaces what you're working on.
- If the file's owner protected it against changes, Fillsign says so and asks: **View only**, **Edit anyway** (if you're allowed to fill it in), or **Don't open**. In View only, nothing can be added or saved until you choose **Edit anyway** in the bar above the page.

## Fill it in

Pick a tool on the toolbar, then click on the page:

| Tool | What it does |
|---|---|
| **Text** | Click and type. Enter starts a new line; Esc or a click elsewhere finishes. **B** (⌘B / Ctrl+B) makes it bold. |
| **Tick** / **Cross** | Click each box. The tool stays on until you pick another or press Esc. |
| **Date** | Adds today's date (DD/MM/YYYY), then goes back to Select with the date selected so you can nudge it into place. |
| **Sign** | Choose or make a signature, then click where it goes. |
| **Shapes** | Rectangle, ellipse, line, arrow, highlight or whiteout: drag, or click for a standard size. Drag either end of a selected line or arrow to point it any way. |
| **Draw** | Freehand. Strokes join into one drawing until you press Done or Esc. |

Each tool also has a letter: **V** Select, **T** Text, **C** Tick, **X** Cross, **D** Date, **S** Sign, **R** Shapes, **P** Draw. They're off unless you turn them on in **Settings → Keyboard**.

**Forms with fillable fields**: fields are tinted blue, and a note says how many there are when the form opens. With Select or Text, click one to fill it (Tick and Cross tick the form's own boxes too): type into a text field (Enter finishes), click a check box or radio button, or pick from a list (in a list that allows several answers, each click adds or removes one). The keyboard works as in a web browser. Tab moves from field to field in the order the form sets (a group of radio buttons is one stop), and selects a text field's contents so typing replaces them. Space or Enter ticks a box, chooses a radio button or opens a list. The arrow keys move round a question's radio buttons and change a list's or drop-down's choice. In a list that allows several answers, the arrows move the dotted outline, Space ticks or unticks that row, and Shift+arrows choose a run of rows. Filled fields are kept in the working copy and the final copy, where they still work in other PDF apps. Read-only fields, such as totals a form works out, can't be changed, and form buttons such as Reset or Submit don't run in Fillsign. Click a signature field to sign straight into it. Some drop-downs also take an answer of your own: choose **Type an answer…**. Empty fields show up in the gap hints before you export. When you export, **Lock the form's fields** (ticked unless you untick it) makes the answers read-only in the final copy, so the person you send it to can't change them. With a screen reader, each field is read out as you reach it, with its answer, and the fields near the page in view can be explored and filled with the screen reader's own commands. The bar above the page shows the field in use and the keys that work in it.

With **Select**, click anything you added to move, resize, recolour or delete it. Shift- or ⌘/Ctrl-click adds to the selection; drag on empty paper to select several. On the page, Tab steps through what you've added; arrow keys nudge (Shift for bigger steps). Every change can be undone (⌘Z / Ctrl+Z), and right after deleting, a note at the bottom offers **Undo** too.

**Paste** (⌘V / Ctrl+V) brings in text or a picture copied from another app. A picture lands in the middle of the page in view; drag a corner to size it.

**Every page**: select your initials (or anything else) and choose **Every page** in the bar above the page to put a copy in the same corner of every other page. Undo removes them all at once.

## Signatures and initials

**Sign** opens the signature window. Switch between **Signature** and **Initials** at the top, then:

- **Draw** with a mouse, trackpad, pen (pressure works) or finger;
- **Type** your name and pick a handwriting style; or
- **Photo or scan**: Fillsign removes the paper so only the ink remains (untick it if parts disappear).

Tick **Keep … on this computer** to reuse it next time. Kept signatures are encrypted with your computer's keychain and never leave it. Remove them in **Settings**. If the keychain won't hand over their key, Fillsign keeps them locked rather than losing them; if the key never comes back, **Start again without them…** in Settings lets you keep new ones.

## Organise pages

Switch to **Organise pages** at the top left. Click pages to select them (Shift-click for a range, ⌘/Ctrl-click to add), then rotate, duplicate, move or delete them from the blue bar, the page's **⋯** menu, or by right-clicking. Drag pages to reorder them, or drop files between pages to insert them. A document always keeps at least one page.

## Save and send

- **Save** (⌘S / Ctrl+S) keeps your **working copy**. Reopen it in Fillsign later and everything you added can still be changed. Other PDF readers show the finished pages.
- **Export final copy…** (⌘E / Ctrl+E) makes the **file to send**. It looks the same in every PDF reader and can't be changed. This is the one to email or upload, especially when it contains your signature.
- After exporting on a Mac, **Share…** on the notice sends the final copy with Mail, Messages, AirDrop and more.
- **Print…** (⌘P / Ctrl+P) prints the document as the final copy looks, through your computer's print dialog (on Linux it opens in your PDF viewer to print from there).
- Before exporting, Fillsign lists **possible gaps** on the pages you've added to: boxes with none ticked, and blanks after labels like "Name:" or "Date:" or along a line, with nothing in them. **Show me** takes you to each one with the right tool ready; **Next gap** moves on and **Back to export** returns. They're hints: Fillsign can't tell what's meant to be filled, and it doesn't look at pages you haven't touched or at boxes and blanks drawn only as table cells.
- Whiteout removes what it covers from the final copy: covered text is deleted and covered parts of a scan are made white, so nothing can be copied or uncovered. If it only partly covers some text or a line, that whole page is saved as a picture, so its text can't be searched or copied. Your working copy keeps everything, so the whiteout can still be moved.

If Fillsign closes unexpectedly, your unsaved changes are offered back the next time you start it. Recovery copies are kept for 30 days (change this in Settings).

## Settings

**Settings** (⌘, / Ctrl+,, or the button on the start screen): appearance (match your computer, light or dark), saved signatures and initials, how long recovery copies are kept, single-letter tool keys, and crash reports. Crash reports are off unless you turn them on (Fillsign also asks once, on its second start). When on, a short report is kept on this computer if something goes wrong. It holds the app version, your system and the code that failed, never your documents, signatures or file names. Next start, **Review and send…** opens it as a GitHub issue you can read before sending.

## Keyboard

The keyboard button on the toolbar (or Help → Keyboard shortcuts) lists every shortcut. F6 moves between the toolbar, page list, page and status bar.
