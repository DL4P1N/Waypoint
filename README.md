# Waypoint

A personal daily assistant for Windows. It sits in the system tray, keeps everything in a local SQLite database, and uses Claude to sort whatever you type into tasks, deadlines, lists, expenses, and notes.

- **To do** with reminders, a "what do I do now?" suggestion, and help getting started on things
- **School**, for anyone with classes: course calendar sync, class schedules and alerts, lecture recording and transcription, study windows, practice questions
- **Shift work and sleep**, for anyone with set hours: work shifts, commute or work-from-home settings, and gentle bedtime reminders before early mornings
- **Budget**: budgets, a pay calendar, paycheck estimates from hourly shifts, "can I afford this?", a wait list, card balances, and a savings goal
- **Phone**: alerts, capture, and slash commands through a private Discord server
- **Server mode**: optionally run everything on an always-on Linux box

## Contents

- [Install](#install)
- [Ask](#ask)
- [To do](#to-do)
- [Notes, lists, and Find anywhere](#notes-lists-and-find-anywhere)
- [Classes, work, and bedtime](#classes-work-and-bedtime)
- [Budget](#budget)
- [Discord](#discord)
- [Settings, data, and backups](#settings-data-and-backups)
- [Running it on a server](#running-it-on-a-server)
- [Project layout](#project-layout)

## Install

1. Install the .NET 10 SDK. It includes the .NET 10 Desktop Runtime, which the installed app runs on. `install.ps1` checks for that runtime first and stops, changing nothing, if it's missing.
2. Install [Claude Code](https://code.claude.com) and sign in with your Claude plan. Sorting runs through `claude -p`, so it uses your plan's limits and needs no API key.
3. Run the installer:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

This builds a small `Waypoint.exe` (the `Install` publish profile, which uses the installed runtime) into `%LOCALAPPDATA%\Programs\Waypoint`, adds a Start Menu shortcut, starts the app, and sets it to run at sign-in. Run it again after changing the code. To try changes without installing, use `dotnet run --project src/Waypoint.App`.

An update can't leave a half-copied install behind:

- Each build is copied whole into `versions\<time>-<commit>` and checked byte for byte against what was built before anything is stopped. A copy installed before versions were kept becomes the first version, so it can be gone back to.
- The running app is asked to close the usual way, so drafts are saved and a recording is finished, and is only stopped by force if it hasn't closed within 20 seconds.
- The new `Waypoint.exe` is copied beside the old one, checked, and switched in by rename, keeping the old one as `Waypoint.previous.exe`. The shortcut and sign-in entry always start the same `Waypoint.exe`. Files the new version doesn't have are removed, and `current.txt` names the version in use.
- If the new version isn't still running after 8 seconds, it's removed and the previous version is put back and started, and the installer says so.
- The last two versions are kept. To go back to the other one at any time:

  ```powershell
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Rollback
  ```

To try the installer itself, give it a folder of its own with `-InstallRoot <folder>`. It then leaves the shortcut and sign-in entry alone and starts that copy as a trial run (`WAYPOINT_TRIAL=1`) on its own data folder (`-DataDir`, or `data` inside that folder), so it never opens your data or meets the installed copy. `-PublishedFolder <folder>` installs an already-built folder instead of building, and `-StartCheckSeconds` changes how long a new version has to stay running.

Left-click the tray icon, or choose **Open Waypoint**, to open the window. A fresh copy shows a **Getting set up** card on To do with four steps: add a first task, add work hours, say how you're paid, and connect Discord. Each line opens the right place and goes away once it's done. **Hide this** puts the card away for good.

## Ask

Type whatever is on your mind into the Ask box on the right of the window, or choose **Ask Waypoint...** in the tray, and press Enter. Ask stays open on every page. Its example prompts can be clicked to fill the box.

Captures are saved before sorting, so nothing is lost when you're offline or out of usage. Anything unsorted is retried at the next capture or app start.

The same box changes things and answers questions:

| Want to | Type |
| --- | --- |
| Change something | "move the midterm to Friday", "cancel laundry", "I finished the paper", "undo that" |
| Decide what's next | "What do I do now?" (also in the tray menu) |
| Split up something big | "break down Paper #2" |
| Get going | "I can't start", "I have 15 minutes", "I'm exhausted", "help me switch to studying" |
| Save your place | "save my spot: stopped on X, next Y", then later "where did I stop?" |
| Catch up | "I missed the quiz" |
| Repeat something | "water the plants every day at 9", "pay rent on the 1st of each month" |
| Take medicine | "take vitamin D at 9 every morning", then later "did I take my meds?" |
| Look back | "what did I need to buy?", "what did I write about the project?", "what did I finish last week?" |
| Change the look | "use the amber theme" |

### Getting started on things

- **"I can't start"** gives you one tiny action, with **Did it**, **Make it smaller**, and **Pause**, which keeps your place (the PC card also has **End**). A reminder's **Start** button does the same.
- **"What do I do now?"** picks the most pressing thing, and breaks it down for you first when it's big.
- **Save my spot** (in the tray, on To do, or typed) remembers where you stopped and what's next. Starting a task with a saved spot tells you where you stopped, makes the saved next step your first action, and opens the saved link.

### Reminder cards

Every card, on the PC and on Discord, keeps the same order and words:

1. **The main action**, such as **Start** or **Done**
2. **10 more minutes** on a card you can start, or **Not now** on a plain reminder, which brings it back once, ten minutes later
3. **Skip**, which clears it with nothing more to come

A card that comes back offers only Start and Skip, so it never turns into a chain. Skip never checks anything off, and the item stays on your list. On the PC, **Save my spot first** opens the Save my spot form, unless you saved a spot in the last hour. Repeating reminders keep Done and a Not now that brings them back after their usual gap.

**"After this."** If a card with Start gets no answer for half an hour (on Discord, or left up on the PC), Waypoint holds that one thing and brings it back once as "Earlier you meant to: ..." with **Start** and **Not now**. It rides on something already showing, whichever comes first: opening the Waypoint window or To do, finishing a session with Did it, tonight's heads-up, or the next morning's briefing. Nothing new is sent for it. Only the newest is kept, so a new one replaces an older one. Once it's been shown, it's gone whatever you choose, and nothing older comes back after it. It's also dropped if you start, finish, move, snooze, or delete that item first, or say its reminder didn't help. Anything held for more than a day is dropped.

**This didn't help.** Reminder cards, the suggestion on To do, the session card, the "Earlier you meant to" card, and each To do row's right-click menu have a small **This didn't help**. It keeps the click straight away, then offers one optional line; **No note** keeps it without one. On Discord the line is a small form. They're listed under **Feedback**, last on Settings, where **Clear all** removes them, with Undo to put them back. Each one keeps where it was, when, the line if you added one, and the item's title, so it still reads after the item is gone. Nothing else about how you use Waypoint is kept, and nothing asks again later.

### Medicine reminders

Type something like "take my meds at 8am every day" or "allergy pill at 10pm daily", and it becomes a repeating reminder marked as a dose. To mark a repeating reminder you already have, right-click it on To do and choose **Mark as a dose** (or **Not a dose** to undo it).

Its card, on the PC and on Discord, reads **Took it**, **Not now** (back once, ten minutes later), and **Skip**. Took it keeps the time for that day and moves the reminder to its next time, the same as Done, and it shows as finished in History. Each day counts once: if it was already marked, from any card, the To do row, `/took`, or typing "I took my vitamin D", you get "Already marked taken at 8:12 AM" and nothing is kept twice.

Ask "did I take my meds?", "did I take vitamin D today?", or "when did I last take my allergy pill?" and the answer comes from what was marked, straight away and without Claude: "Yes, at 8:12 AM today." or "Not marked today. Last marked yesterday at 8:05 AM." With more than one dose, each gets its own line. It only says what was marked, never whether to take one.

There's nothing to keep up. A day that goes by unanswered is simply gone, with nothing counted and nothing coming back about it. Only the taken times are kept, for 30 days.

### Waiting on someone

Type "I emailed Prof. Quill about the extension" or "waiting on HR to reply". Waypoint adds a task for it and stays quiet for a few days, skipping the weekend, so it isn't picked by "I can't start" or the 15-minute button. On the follow-up day you get one reminder.

- **"still waiting"** checks back in a few more days.
- **"draft follow up"** writes a short, polite email to copy. Nothing is ever sent for you.
- **"What am I waiting on?"** lists them all.
- When it's settled, mark it done like anything else.

On To do, **Waiting on** in the column shows every wait, + has a Waiting on form where only Who needs filling in, and right-clicking a task offers **Waiting on someone...**. A wait's own menu has Copy follow-up draft, Still waiting, and Not waiting anymore.

If alert email is connected and a message arrives from them, matched by the address you gave or their name, the wait comes back with one reminder saying they wrote back. Only the sender and subject are looked at.

### Dropping files

Drag a file onto Ask or the Ask Waypoint window: PDFs, Word, Excel, PowerPoint, text files, and pictures with text.

- A **syllabus** becomes exams and deadlines.
- A **card or bank CSV** goes through the import.
- A **pay calendar** shows the dates it found. Choose **Use these** or type yes, and the forecast uses those exact dates.
- **Anything else** is kept as a searchable note.

Files are only read, never run. Account numbers and Social Security numbers are taken out first. A file can add things but can't change or finish what's already there.

## To do

### The suggestion

The top of To do suggests one thing to start, with **Start** and **Not now** and a short line saying why. It starts from fixed guesses and adjusts from the last three months, with nothing to log:

| Guess | Starts at | Learns between |
| --- | --- | --- |
| A step | 20 min | 10 to 30 min |
| A small task | 15 min | 10 to 45 min |
| A first sitting on a deadline or exam | 1 hour | 30 to 90 min |
| Work in a day | 2 hours | 1 to 3 hours |

After five finished sessions of a kind, it uses how long yours usually run, and says so ("usually takes you about 30 min"). If most deadlines get checked off late, they come up a day sooner. If reminders at a time of day almost always get Not now, it offers something short then, unless something is pressing. Only Start and Not now answers on reminder cards are kept for this.

### Study windows

There's nothing to set up. When an exam is within a week, or a deadline within three days, the suggestion card shows a line like "Study window: 3:00 PM to 4:00 PM for Calc midterm (Thu)". It's the first free half hour to an hour today around classes, work, and events, ending where tonight's wind-down starts or at 10 PM. The morning briefing includes it too. For an exam, **Practice questions** makes questions from that class's notes.

It's worked out fresh each time, so a skipped window is simply gone. Nothing is counted, logged, or kept as a streak.

### Working with rows

Rows are grouped by day: **Overdue** for anything open from before today, then Today, Tomorrow, the days of the week, later dates, and No date. Overdue is one quiet group, never red and never counted, and Discord uses the same word.

Right-click a row, or press the menu key, to mark it done, start, reschedule, rename, break down, stop its reminders, copy its title, or delete it. **Start with a tiny step** starts it on its first tiny action straight away, from the fixed list and without Claude, while **Make it smaller** on a session card asks Claude for a smaller one.

- **Delete** always offers Undo, and a deleted open task stays in History as canceled.
- **Rescheduling** something that repeats moves that one time only.
- **Finished** things stay on the list, crossed out, until their date passes (or through the next day if they have none). Then they move to **History**, where unchecking a row puts it back.
- **Course calendar dates** can be overridden ("move the group plan to Friday"). That lasts through syncs, and "use the feed date for the group plan" undoes it.
- **Long captures** are titled by their first sentence. The full text shows under the title while selected, or with **Show full text**.

### Briefings

A short briefing arrives each morning at 7:30, and a summary of the week ahead on Sundays at 6 PM. Both times can be changed (see [settings.json](#settingsjson)).

## Notes, lists, and Find anywhere

The side menu has To do, Notes, Budget, History, and Settings. Your lists are on the Notes page, in its column below the folders. Each page has a column with search and a **+** button for adding things without Ask.

| Shortcut | Does |
| --- | --- |
| Ctrl+1 to Ctrl+5 | Switch page: To do, Notes, Budget, History, Settings |
| Ctrl+F | Search this page |
| Ctrl+N | Add the page's usual kind straight away: a task on To do (a waiting-on item while **Waiting on** is open), an expense on Budget, a note on Notes. **+** still lists every kind. |
| Ctrl+K | Find anywhere (in a note, Ctrl+K is the link box) |
| Ctrl+E | Show a note's Markdown text, or the formatted note again |
| Ctrl+Alt+M | Mark a moment while recording |

### Lists

Lists live in the Notes column, as their own group between the folders and Saved spots, under a small **Lists** label. Shopping is always first; drag the other lists, or use Move up and Move down on their right-click menu or Alt+Up and Alt+Down, to put them in your order. Picking a list shows it where the notes list and the note were, and picking a folder, All, Unfiled, or Saved spots brings the notes back. While a list is open, the search box looks through that list.

Add, edit, check off, and delete directly, or from Ask: "make a packing list: socks, charger", "add milk to shopping", "what's on my shopping list", "check off milk". **+** on Notes has **List** for a new one. Any list can be run again: **Start over** (shown once something is checked) or "start my morning routine" unchecks it and gives you the first step. Rename and Delete list are under **⋯** beside the title and on the list's right-click in the column. Notes can't be dragged onto a list.

### Notes

The Notes column has All, Unfiled, your folders (folders can go inside folders, and can be dragged into your order), your lists, and Saved spots. Drag a note onto a folder to move it. Unless the window is wide, the notes list steps aside while a note is open so the note has room; **Show the notes list** brings it back. Notes save as you type, and stay out of To do until you search for them. Every note is also a Markdown file in `Documents\Waypoint Notes`, with a folder for each notes folder, so you can open them in Obsidian, VS Code, or any editor. Outside changes show up within a few seconds, and a new `.md` file becomes a new note. If a note changes in both places at once, the outside version is kept as its own note. Settings shows the folder and can move it.

#### Writing in a note

A note is written like a word processor document. Bold, italic, strikethrough, underline, code, headings, bulleted and numbered lists (nested too), checklists, quotes, links, and rules show as formatting, with no Markdown marks. Under the hood the note is still plain Markdown in the same `.md` file, so nothing is lost: a table, raw HTML, a footnote, or a fenced code block that the editor can't format shows as a protected block of its Markdown, and **Show Markdown** (Ctrl+E) lets you edit it. Underline is saved as `<u>text</u>`.

A toolbar above the note has a text style menu (Normal, Heading 1 to 3), Bold, Italic, Underline, Strikethrough, Code, Clear formatting, Bulleted list, Numbered list, Checklist, Quote, Link, Undo, and Redo. Each button shows whether the caret or selection already has that formatting. As in Word, the caret has the formatting of the character before it, or at the start of a line the one after it, and typing there continues it. A new line after bold starts plain.

| Keys | Does |
| --- | --- |
| Ctrl+B, Ctrl+I, Ctrl+U | Bold, italic, underline |
| Ctrl+Shift+X | Strikethrough |
| Ctrl+Alt+1 to 3, Ctrl+Alt+0 | Heading 1 to 3, normal text |
| Ctrl+Shift+8, Ctrl+Shift+7, Ctrl+Shift+9 | Bulleted list, numbered list, checklist |
| Ctrl+Space or Ctrl+\ | Clear formatting: takes bold, italic, underline, strikethrough, and code off, and keeps links |
| Ctrl+K | Add, change, or remove a link. Ctrl+click opens it, or starts an email for a mail address. |
| Ctrl+Z, Ctrl+Y | Undo and redo, a burst of typing at a time. They carry on across Show Markdown, so the last change is undone whichever view it was made in. |
| Enter | Continues a list or checklist. On an empty item it ends the list. At the start of a table it adds a line above the table. |
| Tab, Shift+Tab (or Ctrl+], Ctrl+[) | Indent and outdent a list item |
| Backspace at the start of an item | Takes the bullet off first |
| Ctrl+Enter | Tick or untick a checklist item (you can also click its box) |
| Ctrl+Shift+V | Paste as plain text |
| Alt+F10 | Move to the toolbar. Left and Right go between its buttons, Enter or Space uses one, and Esc goes back to the note. |

Typing `# `, `## `, `### `, `- `, `* `, `1. `, `[] `, or `> ` at the start of a line makes a heading, list, checklist, or quote, and `**bold**`, `*italic*`, `~~strike~~`, or `` `code` `` formats when the closing mark is typed. Ctrl+Z right after puts back the marks you typed.

Pasting from Word, Google Docs, or a web page keeps bold, italic, headings, lists, and links, and drops colors, fonts, and sizes. Images are left out. Pasting Markdown text works as Markdown. Pasting a web address over selected text makes the text a link, and pasting several lines into a list item or quote makes each line an item or quote line. Only web and mail addresses can be links: any other link that's pasted, or typed in Show Markdown, becomes its text. Links a note already had when it was opened are left as they are.

### Lecture recording

Choose **Start recording** in the tray, or **Record lecture...** under + on Notes, and pick the microphone, computer audio (for Zoom or a call), or both.

- Turn on **Offer to record when a class starts** under Recording in Settings to get a card at the start of each class. Nothing records unless you choose Record.
- Press **Ctrl+Alt+M**, or **Mark** at the top of Waypoint, to flag a moment that matters.
- Keep the lid open. Waypoint keeps the PC awake, but closing the lid still sleeps it.

When you stop, the recording is transcribed on this PC with Whisper, and only the text goes to Claude to write the note. **Import audio...** under +, or dropping a recording onto Notes, does the same for a file you already have, including Ogg Opus files such as a saved Discord voice message. The first time, Waypoint asks before downloading Whisper and its model (about 1.6 GB) into the data folder.

**Keep the audio** saves a copy with the note for playback. Otherwise the audio is let go once the transcript is saved and covers most of the recording (at least 75%). If it doesn't, the audio is kept with the note and Waypoint says so. Your own imported file is never changed.

**Study** on a class folder makes practice questions from its notes. An exam with no class folder asks which folder to use and remembers it.

### Find anywhere

**Ctrl+K** (outside a note), or the button under the side menu, searches open to-dos, every list, notes, History, and purchases from any month at once, without Claude. Press Enter for the first result or pick one to open it on its page. A list entry shows as Notes and its list, such as "Notes, Shopping list".

## Classes, work, and bedtime

### Calendar feed

Paste your calendar feed link (a `webcal://` link works) under **Connections** in Settings and choose **Save**. It's read straight away and then every hour. Upcoming events show alongside your captures, anything you check off stays checked off, and assignments get reminders planned automatically. **Skip these** on a card stops them for that assignment. The link isn't shown again: paste a new one to replace it, or **Remove** it.

### Classes

Add classes under **Classes** in Settings: name, days, start and end times, and optionally the place and travel minutes. Pick the term's first and last day with the buttons below. **Edit** fills the form to change a class, and **×** removes it. Changes apply straight away.

Leave time is the class start minus travel time. You get a warning 15 minutes before that, then one "Time to leave" message.

### Work hours

Type them into Ask, or set them under Work in Settings:

- "I work Mondays and Wednesdays 9 to 5"
- "working Saturday 8 to 2", "I'm off this Wednesday"
- "I don't work Mondays anymore", "when do I work?"
- "it takes 25 minutes to get to work" (the commute is 15 minutes otherwise)
- Commute settings: set the minutes, or if you work from home, say "I work from home", or tick **I work from home** under Work in Settings. That sets the commute to 0. Shifts then say when work starts ("Work starts: 9:00 AM") and never "leave by". Bedtime still works back from the shift start, and classes still get leave times. Unticking it goes back to 15 minutes.

### Bedtime

The night before a class or shift that starts before noon, Waypoint walks you toward bed with gentle reminders: a heads-up, saving your spot, starting your routine, two wind-down steps you name yourself, a final wrap-up, and bedtime. Bedtime works back from whichever comes first. Other nights get nothing.

- **Wind-down steps**: two steps of your own, each with its own reminder, named under Settings, Bedtime. They start as "Evening step" (75 minutes before bed) and "Wind-down step" (30 minutes before). Untick one there to turn its reminder off. In the file they're `eveningStepName` and `eveningStepMinutes`, and `windDownStepName` and `windDownStepMinutes`, where null turns that step off.
- A small checklist shows in the list during the wind-down. Check steps off there, or say "done" and the step's name, like "done stretch" or "stretch done". Nothing is ever checked off for you.
- "Snooze bedtime" brings the last reminder back once. "No more bedtime reminders tonight" ends them.
- Once you've saved your spot, that reminder stays quiet and the wrap-up says your spot is saved.
- If the PC wakes late, you get only the latest reminder, with anything you missed.
- The window's colors stay the same at night.
- **Late-night check-ins**: on nights with a wind-down, if the PC is still in use after bedtime (keyboard, mouse, or game controller input in the last 15 minutes), a Discord card comes about every 30 minutes, starting 30 minutes after the bedtime reminder. It says the time, the first class or shift tomorrow and when to leave, and how much sleep is still possible if you stop now. While a wind-down step whose reminder is on isn't checked off, the card leads with it and has **Done:** and the step's name to check it off, so there's no separate repeating reminder for them. Its other buttons: **Save my spot and stop** opens the Save my spot form and ends them for the night, **Stop at...** picks a time for one last reminder, and **Not tonight** ends them. A button on an old card only works for the night it was sent. Once the PC has had no input for 15 minutes, nothing more comes that night. "No more bedtime reminders tonight" ends them too. They only go to Discord, never as a PC notification. Waypoint only reads the time since the last input (and, for controllers, only whether one's state changed), only from bedtime until morning, and never which app is open. Once the night's check-ins end, or the next time the app starts or closes after that night, even the time of the last one is cleared. Turn them off under Settings, Bedtime (`lateCheckIns` in the file). Whenever settings.json has a server address, in Server or Local mode, the PC sends that one number to the server about once a minute from 5 minutes before bedtime until morning, since the server may be the one sending alerts. The server keeps only the latest one, in memory. If it hears nothing for 15 minutes after the last input it knows of, it counts the PC as off for the night. An address without http:// or https:// is skipped for this.

## Budget

Each category is weekly or monthly, and new ones are monthly. A weekly budget is the monthly amount spread over the weeks. Type "make groceries monthly", "make wants weekly", "rename wants to fun", or "my budget is $400 a month" to see what's safe to spend today.

### Pay and "can I afford this?"

Say how you're paid ("I get paid on the 1st and 15th, about $850") and the Budget page shows what's expected, what's due before payday, and how low checking gets. If you're paid hourly, enter your hourly pay under Work in Settings and paychecks are estimated from your shifts.

Ask **"can I spend $65?"** to see what a purchase does to checking before the next paycheck, and whether checking stays above the **checking minimum** ($150, the least you keep there). The **cushion** is the separate savings account, with its own $500 goal.

### Wait list

"I want headphones for $80" parks a purchase for 48 hours ("make the wait list 24 hours" changes that). When the wait is over, its card has **Bought** and **Skip**.

### Worth a look

The Budget overview's **Worth a look** says what's going on and what would help: a category over its budget, or "Contoso Card is at 35%. Paying $125.00 brings it down to 30%." for a card over 30% of its limit or over the balance you asked to hear about. Cards with a closing day say when they close.

### Importing

**Import card or bank CSV...**, in the tray or on Budget, adds purchases from an export and opens Purchases, which says how many still need a category. Grocery store purchases go in Groceries. Which bank an export or alert email belongs to comes from the [bank profiles](#bank-profiles).

### Savings goal

Set one goal: "I want to save $500 by March", or **+** then **Savings goal**. A date is optional. Progress counts money added to savings, cushion, and invested balances since you set it. "Can I spend $65?" then also says whether the purchase would push the goal back. Use "how's my savings goal?" to check and "drop my savings goal" to remove it.

### Transfers and investing

Scheduled transfers are added like bills: "Fabrikam transfer is $100 on the 3rd" or "invest $50 on the 10th". Until payday shows each from the last five weeks as **Confirmed** or **Not seen yet**, checked against imported checking CSVs and Fabrikam deposit emails only. A transfer gets 4 days to show up. For investing, Waypoint sees the money move, not the purchase inside Fabrikam.

### Net worth, history, and credit score

- **Net worth** is recorded once a day whenever the budget is worked out. Balances shows the last 90 days and each month's change.
- **Monthly summary** compares a month's spending by category with the month before, plus paychecks, income, and net worth. Or ask "how did I do last month?" or "how was October?"
- **"Show spending patterns"** looks at your habits, only when you ask. Spending review ends with **What's helping**: how many parked purchases you let go, and how often a category came down after going over.
- **Credit score**: "credit score 720 on ScoreCheck", or **+** then **Credit score**. "What's my credit score been?" lists them. Waypoint only keeps what you type and never looks a score up.

## Discord

Discord gives you alerts and capture on your phone. With it set up, every alert goes there alone. If Discord can't be reached after about 5 minutes of retries, the alert shows on the PC instead.

### Setup

1. Create a private server with channels for capture, alerts, and budget. To-do, lists, notes, and history channels are optional, for pinned views. For notes and lists on the phone, also make two **Forum** channels, one for notes and one for lists (see [Notes and lists forums](#notes-and-lists-forums)).
2. At the [Discord Developer Portal](https://discord.com/developers/applications), create an application, add a bot, turn on **Message Content Intent**, and copy its token.
3. Under **OAuth2 > URL Generator**, pick the `bot` and `applications.commands` scopes, then the View Channels, Read Message History, Send Messages, Embed Links, Add Reactions, and Pin Messages permissions. Open the link to invite the bot. (Where Pin Messages isn't listed, Manage Messages covers it. A bot invited before needs the new link once.) The notes and history channels need nothing more: forms, menus, and Back and Next buttons work with these permissions. For the forums, also pick **Send Messages in Threads**, **Manage Threads**, **Manage Messages**, and **Manage Channels**, and invite the bot again with the new link. Create Public Threads isn't needed, since a forum post only needs Send Messages.
4. Turn on Developer Mode in Discord, then right-click to copy your user ID and each channel ID.
5. Enter them under **Discord** in Settings, choose **Save**, then **Send test message**. The two forum IDs go in the optional **Forum channel IDs** boxes.

Keep the token private. A token typed in Settings is kept encrypted on the PC. Alerts are sent from the PC, so it has to be on (unless you use [server mode](#running-it-on-a-server)).

### Capture channel

Anything you type there arrives right away while the PC is on and gets its answer as a reply. Questions like "what do I do now?" or "can I spend $40?" work too. Messages sent while the PC was off are picked up when it's back. Pictures, PDFs, Office files, and text files up to 10 MB are read like a dropped file, and a bank CSV goes through the import.

A voice message, or an audio file up to 5 minutes, is turned into text on the PC with the same Whisper used for lectures, then taken exactly as if you'd typed it. The reply shows what was heard, so a mis-hearing is easy to spot. Until speech to text is set up (the first **Import audio...** on Notes asks), or if it doesn't work that time, the voice note is kept in the data folder and read later on its own, and the reply says so once. The audio is deleted once its words are saved. Anything over 5 minutes is a recording: add it on the Notes page on the PC.

### Slash commands

These answer without waiting on Claude. `/help` lists them.

| Command | Does |
| --- | --- |
| `/now` | The next thing to do, with a Start button |
| `/today`, `/todo` | Today, or the week |
| `/budget`, `/afford` | Budget, and whether you can afford something |
| `/lists`, `/waiting` | Your lists, and what you're waiting on |
| `/add` | Add an item with a title and an optional date, time, and kind. A time with no kind makes a reminder. |
| `/done`, `/move`, `/start` | Suggest items as you type, and ask which one when the name isn't exact. `/move` can change only the time. `/start` alone starts on the most pressing thing. |
| `/took` | Marks a dose taken, once for its day. It suggests only doses, and needs no name when there's just one. |
| `/spent` | Add a purchase |
| `/check`, `/listadd` | Check off or add to a list (`/listadd` takes several with commas between) |
| `/new` | Opens a form to add a task, deadline, reminder, event, or exam, with a day, time, end time for events, and class or filter, the same as the **+** form on To do |
| `/notes` | Your note folders, one folder, or a search of your notes |
| `/history` | What was finished, canceled, reopened, or went past, with the History page's filters, a search, and a menu to reopen one |
| `/search` | Looks through To do, lists, notes, History, and purchases the way Find anywhere does. Pick a result to open it. |
| `/purchases` | This month's purchases, newest first, with the month before a press away. The budget view has a Purchases button too. |
| `/settings` | Work hours, bedtime, theme, and how each connection is doing, with forms to change bedtime and work hours. Tokens and links are never shown or asked for. |
| `/order` | Move To do filters, lists, or note folders up or down, the same as dragging them on the PC |
| `/snooze`, `/save-spot` | Snooze, or save your spot. `/save-spot` with nothing typed opens a short form. |
| `/pinhere`, `/unpin` | Keep a pinned view in this channel, or stop |

Dates can be typed as today, tomorrow, friday, next monday, in 3 days, 11/14, Nov 14, or 2026-11-14, and times as 3pm, 3:30 pm, or 15:30.

On alerts, reacting ✅ or ⏰, or replying `done` or `snooze`, works like the buttons. A handled alert turns gray. Only your user ID is listened to.

### Pinned views

Pinned views keep To do for the week, Waiting on, Budget, each list, the notes folder index, and History in one message each that's edited in place, without notifying your phone. `toDoChannelId`, `listsChannelId`, `notesChannelId`, and `historyChannelId` place them automatically, or use `/pinhere`. The To do view has a menu to mark things done, and each list has one to check things off. Views update within about a minute, or right away after a change from Discord.

A view too long for one Discord message shows a page at a time with Back and Next, and says which page it's on. A view that changes goes back to its first page.

### Notes channel

The pinned notes view lists every folder with how many notes it holds, with **Search notes** and **New note**. Pick a folder to see its notes, and a note to read it, a page at a time, with tables written as lines. What's opened from the pinned view comes as a reply only you see, so the index stays put.

A file sent in the notes channel becomes a note the same way dropping it on the Notes page does, including notes from PDFs and slides, and the note is shown when its notes are written. Recordings sent there get a reply that phone recordings aren't supported yet. Files sent there are only seen while Waypoint is connected, so send them again if it was off.

### Notes and lists forums

Notes and lists can each live in a Discord forum channel, one post per note and one per list, which reads well on a phone. Set the notes forum and the lists forum under **Discord** in Settings, or as `notesForumChannelId` and `listsForumChannelId` in settings.json. The pinned notes view and pinned lists keep working beside them.

- **Notes forum:** every note gets a post named after it, tagged with its folder (a folder inside another is tagged "Parent / Child" when that fits in 20 characters). Waypoint makes and keeps the tags itself. A forum holds at most 20 tags, so past 20 folders the rest have no tag, and Settings says so. The note's text shows in Waypoint's own messages, split over several when it's long, with tables written as lines. Sources and transcripts stay on the PC and show only as a line such as "Sources: 2 files, kept on the PC".
- **Typing in a note's post** adds what you type at the end of that note as a new paragraph, the same as typing it on the PC, so its `.md` file and History follow. The post updates, and your message is taken away once it's saved. A file sent there joins that note's Sources, the same as dropping it on the note.
- **Edit** under each part opens that part's Markdown in a form (a part holds at most 3,000 characters, so there's room to add). Saving replaces just that part, straight away. If that part changed since the form opened, nothing is replaced: the lines you changed or added go at the end of the note under a "From Discord" line, and the reply says so. If it can't be saved at all, the reply holds what you wrote, ready to copy.
- The posts show checkboxes as ☐ and ☑, the way lists do, and underlined text as Discord's underline.
- **A new post** in the notes forum becomes a note: its name is the title, its first tag picks the folder (no tag means Unfiled), and its first message is the text. Files in it become the note's sources, and with no text, notes are written from them. Waypoint then shows the note in its own messages in that post.
- **Lists forum:** every list gets a post, Shopping first, with a menu to check things off (25 at a time, with Back and Next) and Start over once something is checked off. Typing in a list's post adds each line, or each part between commas, as an entry, the same as `/listadd`. A comma inside a number, as in 1,000 piece, keeps the entry whole. Back and Next keep Start over and the line about typing there. A new post makes a new list, with its first message's lines as entries.
- Changes on the PC (edits, new notes, renames, folder moves, list changes) show in the posts within about a minute, and only messages whose text changed are edited. A new title waits until it has stayed the same for about two minutes, since each rename leaves a "changed the post title" line in the post. Renaming a list keeps its post. Waypoint's own notices in a post (a file that couldn't be added, the ✅ hint) are cleared away after an hour, or sooner when a new part of the note goes under them. A post deleted on Discord is made again; the note or list is never touched. Deleting a note or list on the PC deletes its post, since it can be brought back from History, and then gets its post back. If something typed in that post hasn't been read yet, the post is archived instead, with a message saying it wasn't added, so those words aren't lost. Typing in a post whose note was deleted gets the same answer and stays where it is.
- When a note's post has to be made again somewhere else (its first message was deleted, or the notes forum changed in Settings), the old post gets a link to the new one and is archived. Anything typed in the old post still goes to the note. A new post in the lists forum named like a list that already has one (any capitals) adds its lines to that list and links to its post.
- Without Manage Messages, saved messages get a ✅ instead of being taken away, and the post says once which permission would clear them. Without Manage Channels, posts still work but folders get no tags. Without Manage Threads, posts you started can't be renamed or deleted by Waypoint.
- Only your user ID is listened to, and only what you type: Discord's own notices, such as "changed the post title", are left alone. Messages typed while Waypoint was off are read within a few minutes once it's back, even when there are many. A post or message Discord can't hand over right then (a failed read or a file that won't download) is left in place and tried again, never taken as empty.

### History channel

The pinned History view shows recent activity with **All**, **Completed**, **Canceled**, **Reopened**, and **Past** buttons, **Search**, and a **Reopen one** menu that puts something back the same way the History page does.

### Payment app repayments

When someone pays you back on your payment app (Tailspin Pay in the built-in [bank profiles](#bank-profiles)), Waypoint can count it against the purchase. Forward the app's email to your alerts inbox, and put the address it's forwarded from under **Alert email** in Settings. Alerts only count from the sender domains in the bank profiles, or their subdomains, and only when Outlook says the email passed DMARC.

If one recent food purchase clearly fits, it's marked repaid. Otherwise a card in the capture channel asks which purchase it was, with a button for each of up to four recent purchases and **Not a repayment**. You can also reply to the card with the purchase's name ("Trader Joes" finds "TRADER JOES #123"), "not a repayment", or "skip", or type "that was Chipotle" in Ask.

## Settings, data, and backups

### Settings page

Settings has sections for Look, Work, Bedtime, Classes, Notes and recording, Discord, and Connections, and a search box. Under **Look**, pick a theme (gray, blue, amber, teal, or mono) and **Less see-through** for a nearly solid window background. Without it, windows and menus use acrylic like Windows 11's own menus: what's behind shows through, heavily blurred and softened toward gray, under a light dark tint that keeps text readable even over a white page. Under **Bedtime**, the reminder and check-in ticks save as soon as you click them; the times and routine steps keep their **Save** button.

### settings.json

Everything can also go in `%LOCALAPPDATA%\Waypoint\settings.json` (restart Waypoint after editing). What's saved on the Settings page is used over the file.

```json
{
  "calendarFeedUrl": "https://example.com/your-calendar.ics",
  "workCommuteMinutes": 15,
  "briefingTime": "07:30",
  "weeklyResetTime": "18:00",
  "schedule": {
    "termStart": "2026-09-14",
    "termEnd": "2026-12-18",
    "noClassDates": ["2026-10-16"],
    "firstWarningMinutes": 15,
    "meetings": [
      { "course": "CS 101", "room": "ABC 100", "days": ["Monday", "Wednesday"], "start": "13:00", "end": "14:20", "travelMinutes": 15 }
    ]
  },
  "bedtime": {
    "sleepHours": 8,
    "readyMinutes": 45,
    "eveningStepName": "Evening step",
    "eveningStepMinutes": 75,
    "windDownStepName": "Wind-down step",
    "windDownStepMinutes": 30,
    "latestBedtime": "01:00",
    "headsUpMinutes": 90,
    "saveSpotMinutes": 60,
    "routineMinutes": 45,
    "wrapUpMinutes": 15,
    "bedMinutes": 0,
    "snoozeMinutes": 15,
    "quietAfterWrapUp": false,
    "lateCheckIns": true,
    "routineSteps": []
  },
  "discord": {
    "botToken": "your bot token",
    "ownerUserId": "your user id",
    "captureChannelId": "...",
    "alertsChannelId": "...",
    "budgetChannelId": "...",
    "toDoChannelId": "optional",
    "listsChannelId": "optional",
    "notesChannelId": "optional",
    "historyChannelId": "optional",
    "notesForumChannelId": "optional",
    "listsForumChannelId": "optional"
  }
}
```

The values shown are the defaults where there is one.

- **Classes**: once classes are saved in Settings, that list is used instead of the file's (it starts from the file's). `noClassDates` always comes from the file.
- **Bedtime**: each `Minutes` value counts back from bedtime, and `null` turns that reminder off. `routineSteps` adds your own checklist steps. `quietAfterWrapUp` holds purchase alerts, assignment nudges, and briefings until morning after the wrap-up, while class alerts, exams, and reminders you asked for still come through.

### Bank profiles

The CSV importers and alert email readers know a bank for each of five parts: checking, a card with labeled alert emails, a card whose exports have a type column, a brokerage, and a payment app. Built in, these are example banks (Northwind Bank, Contoso Card, Lucerne Card, Fabrikam, and Tailspin Pay) at `example.com` domains. To map them to your own bank's formats, copy [`bank-profiles.example.json`](bank-profiles.example.json) to `bank-profiles.json` next to `settings.json` (in `%LOCALAPPDATA%\Waypoint`, or the server's data folder), and set each one's account name, sender domains, CSV headers, and statement wording. The wording settings include how checking purchases start and end in the checking export (`purchasePrefix` and `referenceSuffix`, regular expressions), the words before the balance in a checking alert (`balanceWording`), the card alert's subject (`alertSubject`), how a card's own export shows payments and the purchase date (`exportPaymentDescriptors` and `csvDateHeaders`), the rewards card export's DEBIT or CREDIT column (`typeColumn`), and how the payment app's emails word a payment to you, money you sent, and a note (`receivedPattern` and `outgoingPattern`, regular expressions, then `noteAfter`, `noteLabels`, and `notNoteWords`). `merchantNames` adds readable names for merchants, as pairs of statement text and the name to show, tried before the built-in ones. The example file explains each one. It's read at startup, and anything left out keeps its built-in value. The names are the account names purchases are kept under, so keep them the same as your accounts in Waypoint. `bank-profiles.json` is in `.gitignore`.

### Data and backups

Data lives in `%LOCALAPPDATA%\Waypoint`. Waypoint backs up the database once a day into its `backups` folder and keeps 14 days. **Back up now**, **Restore a backup...**, and **Export notes...** are in the tray menu. A restore saves the current data first, so a wrong pick can be undone. This didn't help notes are in the database, so backups keep them, and Export notes also writes them to `Feedback.txt`. Problems are shown in plain words, with the details in `errors.log` in the data folder.

### Environment variables

| Variable | Does |
| --- | --- |
| `WAYPOINT_DATA_DIR` | Use a different data folder. A copy with its own folder runs beside the installed one. |
| `WAYPOINT_TRIAL=1` | With its own data folder, a rehearsal: notes stay in that folder, the start-with-Windows entry is left alone, and no reminders, alerts, Discord, email, calendar, or record offers start. Settings says "Trial run: alerts and connections are off." |
| `WAYPOINT_TRIAL_ALERTS=1` | In a trial run, turns reminders and alerts back on, and nothing else |

## Running it on a server

Waypoint can run on an always-on Linux machine, so reminders, Discord, calendar sync, email, sorting, and backups keep going while the PC is off. The server owns the one database, and the PC app talks to it over Tailscale. The PC keeps working on its own until you switch it. Lecture recording and Whisper stay on the PC.

### 1. Build and copy it

On the PC:

```powershell
dotnet publish src/Waypoint.Server -c Release -o publish/server
scp -r publish/server deploy/linux you@waypoint-server:~/
```

### 2. Install on Ubuntu Server

```bash
cd ~/linux
chmod +x install.sh
sudo ./install.sh ~/server
```

`install.sh` installs the .NET 10 runtime (adding `ppa:dotnet/backports`, or Microsoft's feed for your Ubuntu release, when Ubuntu's own archive doesn't have it), SQLite, `age`, `tesseract-ocr`, and `poppler-utils`, shows the time zone, turns on automatic security updates, creates a `waypoint` user, and installs the `waypoint` service (restarts on failure and at boot) and the nightly backup. Run it again after publishing a new version. It never replaces `settings.json` or anything in `/etc/waypoint`.

Each version goes in a folder of its own under `/opt/waypoint/releases`, and `/opt/waypoint/current` points at the one in use, so a new version is never copied over a running one. Installing a new version stops the server, copies `waypoint.db` with `sqlite3 .backup` to `/var/backups/waypoint` (the last five are kept), points `current` at the new folder, and starts it. If it doesn't answer `/health` within about a minute, `current` goes back to the previous version, that copy of `waypoint.db` is put back, the previous version is started again, and the script ends with an error. The new version and the one before it are kept, and older ones are removed. An install from before this layout has its files moved into a release folder of their own the first time.

Two things it leaves off until you're ready:

- **Automatic reboots.** Updates install themselves, but the machine never reboots on its own, because with an encryption passphrase it would sit at the unlock prompt and reminders would stop. When `/var/run/reboot-required` exists, reboot by hand while you can unlock it. Once the disk unlocks by itself (see Full-disk encryption and reboots below), run `sudo AUTO_REBOOT=yes ./install.sh ~/server` to allow reboots at 4:30 AM.
- **The nightly backup timer.** It's turned on only once `AGE_RECIPIENT` and `BACKUP_DIR` are set in `/etc/waypoint/backup.env` and `BACKUP_DIR` is mounted, so it never fails every night unnoticed. Until then the script says what's missing. See Backups and monitoring.

Then, by hand:

1. **Tailscale.** `curl -fsSL https://tailscale.com/install.sh | sh`, then `sudo tailscale up` and sign in. Put the address from `tailscale ip -4` in `/etc/waypoint/waypoint.env` as `Waypoint__Listen=http://100.x.y.z:5280`, then `sudo systemctl restart waypoint`. Until then it only answers on the server itself. Use the IP, not the MagicDNS name: a name or `0.0.0.0` would listen on every network, so they're refused unless `Waypoint__AllowAllInterfaces=true`. Plain HTTP on any address other than the server itself or Tailscale (`100.64.0.0/10` or `fd7a:115c:a1e0::/48`), such as a home network or public address, a name, or `0.0.0.0`, is refused too, since anyone who could reach it would see what's sent, the pairing token included. `Waypoint__AllowPublicHttp=true` allows it, and the server then warns about it at every start and in `/health`. No public ports are needed.
2. **Claude Code.** Sorting runs `claude -p` as the service user: `sudo -u waypoint -H bash -lc 'curl -fsSL https://claude.ai/install.sh | bash'`, then `sudo -u waypoint -H bash -lc 'claude'` and sign in. If it's installed somewhere unusual, set `WAYPOINT_CLAUDE_PATH`, inside `/home/waypoint` or a system folder. Check it stays signed in after a reboot, and watch `journalctl -u waypoint` after sending a capture.

   **Claude sign-in, if it doesn't last unattended:** run `sudo -u waypoint -H bash -lc 'claude setup-token'`, which signs in with your plan and prints a long-lived token. Put it in `/etc/waypoint/waypoint.env` as `CLAUDE_CODE_OAUTH_TOKEN=<the token>` (that file is readable only by root and the `waypoint` group), then `sudo systemctl restart waypoint`. Waypoint keeps that variable when it runs `claude`, and still removes `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN`, so nothing is ever billed to an API key. Never put the token in `settings.json` or anything that's backed up unencrypted.
3. **Time zone.** `timedatectl` should say your zone; set it with `sudo timedatectl set-timezone <Area/City>` (for example America/Chicago). Reminders, the backup timer, and the journal all follow it. If `Waypoint__TimeZone` is set to a different zone, that mismatch is logged and shown on the PC.
4. **Full-disk encryption** is chosen at install. See below for letting it reboot unattended.
5. **BIOS:** restore power after a power loss, and no sleep. A small UPS helps.

#### Full-disk encryption and reboots

With a passphrase, every reboot (a power cut, or an update) waits at the unlock prompt until someone types it, and reminders stop meanwhile. Pick one of these before turning on automatic reboots, and keep the passphrase as the fallback:

- **TPM unlock (recommended).** The disk unlocks itself on this machine only. On Ubuntu 24.04, which uses initramfs-tools, clevis is the simplest way:

  ```bash
  lsblk -o NAME,TYPE,FSTYPE        # the partition with FSTYPE crypto_LUKS, such as /dev/nvme0n1p3
  sudo apt install clevis clevis-luks clevis-tpm2 clevis-initramfs
  sudo clevis luks bind -d /dev/nvme0n1p3 tpm2 '{"pcr_ids":"7"}'   # asks for the passphrase once
  sudo update-initramfs -u -k all
  sudo reboot                       # it should come up without asking
  ```

  `systemd-cryptenroll --tpm2-device=auto` does the same on systems whose initrd runs systemd (dracut), but Ubuntu's default initrd doesn't read it. After a firmware or Secure Boot change, the TPM may refuse and the passphrase is asked for again. Run the `clevis luks bind` line again afterward.
- **Remote unlock with dropbear.** An SSH server in the initrd lets you type the passphrase from another machine: `sudo apt install dropbear-initramfs`, put your SSH public key in `/etc/dropbear/initramfs/authorized_keys`, set `DROPBEAR_OPTIONS="-p 2222 -s -j -k"` in `/etc/dropbear/initramfs/dropbear.conf`, and run `sudo update-initramfs -u`. After a reboot, `ssh -p 2222 root@<its home network address>` and run `cryptroot-unlock`. Tailscale isn't running yet at that point, so this only works from the home network, and someone still has to do it.

Then run `sudo AUTO_REBOOT=yes ./install.sh ~/server` to allow reboots at 4:30 AM.

### 3. Move the data over, once

Do these in order. The PC stays stopped from step 1 until step 6, so the PC and the server's Discord never run at once, and nothing written in between is lost.

1. **Stop Waypoint on the PC.** Choose **Exit** in the tray menu, and check it's gone from Task Manager.
2. **Copy the data while it's stopped.** Recent changes can still be in `waypoint.db-wal` beside the database, so copy it and `waypoint.db-shm` together with `waypoint.db`, never the database alone:

   ```powershell
   ssh you@waypoint-server mkdir -p move
   $from = "$env:LOCALAPPDATA\Waypoint"
   $files = 'waypoint.db', 'waypoint.db-wal', 'waypoint.db-shm', 'settings.json' | ForEach-Object { Join-Path $from $_ } | Where-Object { Test-Path $_ }
   scp $files you@waypoint-server:~/move/
   ```

3. **Point the PC at the server now, before the server has your Discord settings.** Add this to `settings.json` on the PC, but don't start Waypoint yet:

   ```json
   { "server": { "address": "http://100.x.y.z:5280", "mode": "Server" } }
   ```

   Server mode with no address doesn't fall back to running everything on the PC. It sends nothing and asks for the address in the tray and the status line.
4. **On the server,** fold any `-wal` changes into one file with SQLite's online backup, check it, then put it in place. The `-wal` and `-shm` files left by the empty database the server made at its first start have to go too, or SQLite would apply them to your data:

   ```bash
   cd ~/move
   sqlite3 waypoint.db ".backup $HOME/move/waypoint-whole.db"
   sqlite3 waypoint-whole.db "PRAGMA integrity_check;"     # should say ok
   sudo systemctl stop waypoint
   sudo rm -f /var/lib/waypoint/waypoint.db-wal /var/lib/waypoint/waypoint.db-shm
   sudo install -o waypoint -g waypoint -m 600 waypoint-whole.db /var/lib/waypoint/waypoint.db
   sudo install -o waypoint -g waypoint -m 600 settings.json /var/lib/waypoint/settings.json
   sudo systemctl start waypoint
   cd ~ && rm -r ~/move
   ```

5. Keep the PC's copy in `%LOCALAPPDATA%\Waypoint` where it is. It's the way back.
6. Start Waypoint on the PC and pair it, as in the next step.

### 4. Pair the PC and switch it over

1. Read the pairing token, written once when the server first starts: `sudo cat /var/lib/waypoint/pairing-token.txt`. Delete the file once paired. Only its hash is kept. To make a new one, delete `/var/lib/waypoint/server-token.sha256` and restart.
2. Start Waypoint on the PC. With `"mode": "Server"` and the address from step 3 above, it uses the server.
3. Choose **Pair with server...** in the tray and paste the token. It's kept encrypted to your Windows user.

#### Going back to Local

The server wins over a PC for Discord, so stop it first, and bring its data back, or everything changed on the server since the move is lost:

1. On the server: `sudo systemctl disable --now waypoint`, then copy the database out with the online backup:

   ```bash
   sudo -u waypoint sqlite3 /var/lib/waypoint/waypoint.db ".backup /var/lib/waypoint/waypoint-back.db"
   sudo install -o "$USER" -m 600 /var/lib/waypoint/waypoint-back.db ~/waypoint.db
   sudo rm /var/lib/waypoint/waypoint-back.db
   ```

2. On the PC: **Exit** Waypoint. In `%LOCALAPPDATA%\Waypoint`, rename `waypoint.db` to `waypoint.db.before-server` and delete any `waypoint.db-wal` and `waypoint.db-shm`. Then `scp you@waypoint-server:~/waypoint.db "$env:LOCALAPPDATA\Waypoint\"` and remove `~/waypoint.db` on the server.
3. Set `"mode": "Local"` in the PC's `settings.json` and start Waypoint.

The PC's Discord starts within about 7 minutes, once the server's pinned note goes stale. If you skip step 1's copy and keep the PC's old database, the first Local start moves Discord's reading place to now, so captures the server already handled aren't taken again. Changes made on the server are then only in the server's database and its backups.

### 5. Sign in again on the server

- **Alert email**: the PC's sign-in is encrypted to your Windows user, so choose **Connect alert email** in the tray. The PC shows the code and opens Microsoft's page, and the server finishes the sign-in.
- **Discord**: IDs move with the database, but re-enter the bot token in Settings and choose **Save**. It goes to `/var/lib/waypoint/discord.token`, readable only by the service user, and is never sent back. `discord.botToken` in the server's `settings.json` works too. Pictures and scanned PDFs sent to the capture channel are read with `tesseract` and `pdftoppm`. Whisper doesn't run on the server, so voice notes sent there are kept unread in `/var/lib/waypoint/voice-notes`, and the reply says so.

### How server mode works

The PC runs no alerts, Discord, calendar, email, or backups, so nothing is sent twice. Every page goes through the server.

Only one copy ever uses the bot. The copy running Discord keeps a pinned note in the alerts channel saying where it runs, updated every 2 minutes, and every copy reads it first. A PC left in Local mode that finds the server's note sends no reminders or alerts and reads nothing from Discord, and says "Discord is running on ..." in the tray, the status line, and Settings. The server always wins over a PC. A copy takes over only once the other's note is more than 7 minutes old. The bot needs the Pin Messages and Read Message History permissions in the alerts channel for this, and Settings says so if it can't check.

What lives where:

| On the server | On the PC |
| --- | --- |
| The database, work hours, pay, classes, bedtime, Discord, alert email, calendar feed | Theme, notes folder location, recording |
| Notes from files and recordings, edits, and practice questions, written with `claude -p` | Recording and Whisper transcription. Only the text, and audio if kept, is sent. |
| CSV import | Picking and reading the CSV |
| Backups, restores, and notes export (from the tray) | Picking a restore, and unpacking the export zip |

- **Classes and bedtime** come from the server, checked each time the PC syncs. The last copy is kept while the server is away.
- **Notes folder**: `Documents\Waypoint Notes` follows the server's notes, checked every 15 seconds. When both sides changed, both versions are kept.
- **Attachments**: kept recordings go to `/var/lib/waypoint/attachments`, up to 300 MB each (`Waypoint__MaxAttachmentBytes`). Other requests are limited to 10 MB (`Waypoint__MaxRequestBytes`), and anything without the pairing token is turned away before it's read.

**While the server is away**, the tray and status line say so, and every page shows what it last had, even after a restart. Those last reads are kept in `server-reads.json`, encrypted to your Windows user. Captures wait in `server-queue.json`, and typing in notes waits in `server-notes.json`. If a note changed on the server meanwhile, both versions are kept, and the PC's ends in "(edited on this PC)". A finished transcript waits and is sent once the server is back. Other changes say they can't be made until then.

### Backups and monitoring

- **Nightly backup.** Make a key on another machine with `age-keygen -o waypoint-backup.key` and keep it safe off the server, since it's the only way to read the backups. Put its public key in `AGE_RECIPIENT` in `/etc/waypoint/backup.env`, and set `BACKUP_DIR` to a second disk. Give the backup group the folder: `sudo chgrp waypoint-backup /mnt/waypoint-backup && sudo chmod 2770 /mnt/waypoint-backup` (on FAT or exFAT, mount with `gid=waypoint-backup,umask=007`). The archive is streamed straight into `age` and holds the database, `settings.json`, `bank-profiles.json` if there is one, the Outlook and Discord sign-ins, the pairing token's hash, and attachments. `RCLONE_REMOTE` can copy each file offsite, after `sudo -u waypoint -g waypoint-backup env RCLONE_CONFIG=/etc/waypoint/rclone/rclone.conf rclone config`. Those settings stay in `/etc/waypoint/rclone`, which only the backup unit's group can open, so the running server can't read the remote's sign-in. The remote keeps the newest `RCLONE_KEEP` backups (60 unless set), and older ones are removed from it after each backup. Run one now with `sudo systemctl start waypoint-backup`.
- **Turn the timer on.** Mount the backup disk (add it to `/etc/fstab` so it mounts at boot), fill in `/etc/waypoint/backup.env`, then run `sudo ./install.sh ~/server` again. It turns the nightly timer on only when the settings are there and `BACKUP_DIR` is mounted, and otherwise says what's missing. A backup that fails says why in `journalctl -u waypoint-backup`, ends with the unit marked failed (`systemctl status waypoint-backup`), and pings `BACKUP_PING_URL` with `/fail` added, so healthchecks.io alerts the phone at once.
- **Test a restore** before relying on it, and again every few months. On the machine with the key, copy a backup over and run `deploy/linux/waypoint-restore-test.sh waypoint-backup.key waypoint-2026-01-01-0315.tar.gz.age`. It decrypts into a temporary folder, runs SQLite's integrity check, prints how many items and captures it holds to compare with the app, and cleans up. It ends non-zero if anything fails. To look around further, open the restored `waypoint.db` with the PC app pointed at a copy by `WAYPOINT_DATA_DIR`, with `WAYPOINT_TRIAL=1` so it sends nothing.
- **Heartbeat.** Make a check at healthchecks.io (or similar) that notifies your phone, and put its ping link in `Waypoint__HeartbeatUrl`. The server only pings while reminders are running and, when Discord is set up, while it works: sends that keep failing or a Discord connection that stays down for more than 10 minutes hold the ping back, so the phone hears about a bad bot token too. `BACKUP_PING_URL` does the same for backups. `/health` answers without a token and says only whether the server is doing its job. Server logs go to the journal (`journalctl -u waypoint`), never to the Windows event log.
- Keep phone alarms for mornings that matter until the server has proven itself.

### Not built yet

**A phone token that can only read.** Today the one pairing token can do everything, so a phone holding it would be as trusted as the PC. A second token could be made beside it, say with a tray item **Make a phone token...**, and kept the same way, only its hash on the server, in a file of its own. Requests with it would get the `GET` routes for the day's pages and nothing else: no captures, edits, settings, restores, or backups, and no `/api/notes` writes. The check would sit in the same place the pairing token is checked, before routing, as a short list of allowed method and path pairs, so a new route is closed to the phone until it's added on purpose. It could be withdrawn on its own by deleting its file, without pairing the PC again. A later step could let it mark a reminder done, as its own allowed route.

## Project layout

| Path | What's there |
| --- | --- |
| `src/Waypoint.Core` | Database, capture flow, and Claude sorter |
| `src/Waypoint.App` | WPF tray app and windows |
| `src/Waypoint.Server` | The headless server, its private API, and background work on Linux |
| `deploy/linux` | systemd units, install script, and backup script |
| `tests/Waypoint.Core.Tests` | Core library tests |
| `tests/Waypoint.App.Tests` | In-process WPF tests, with no real input |
| `tests/Waypoint.Server.Tests` | API tests against an in-memory server |

Run the tests with `dotnet test`.
