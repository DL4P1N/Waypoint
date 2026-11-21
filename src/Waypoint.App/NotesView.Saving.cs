using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using Waypoint.Core;
using Waypoint.Core.Capture;
using Waypoint.Core.Data;
using Waypoint.Core.Notes;
using Waypoint.Core.Transcription;

namespace Waypoint.App;

/// <summary>Saving the open note: saves sent off the window's thread in server mode, the ones on their way, and writing the draft.</summary>
public partial class NotesView
{
    // Editor

    private void Save()
    {
        if (Flush())
        {
            Refresh();
        }
    }

    /// <summary>
    /// A save to the server of a note that's already there. It's sent off the window's thread, so a slow server never
    /// holds up typing or a click. Saves go in the page's order, so a later one, or a delete, never overtakes it.
    /// </summary>
    private sealed record BackgroundSave(long Id, string Title, string Body, Task<long> Saving, NewNoteSave? Plan = null)
    {
        /// <summary>
        /// The title it sends: typed in a new note with no title of its own, the class title the note was made with.
        /// It's kept with the save, so it holds even once the note has been opened again.
        /// </summary>
        public string SentTitle => Plan?.Title is { } given && string.IsNullOrWhiteSpace(Title) ? given : Title;
    }

    /// <summary>The saves on their way, oldest first.</summary>
    private readonly List<BackgroundSave> _saves = [];

    /// <summary>
    /// Notes found gone from the server while a save was on its way, and the note each was made again as, so a later
    /// save goes there too and the note is only made once. Only saves use it, one at a time.
    /// </summary>
    private readonly ConcurrentDictionary<long, long> _madeAgain = new();

    /// <summary>
    /// A new note in server mode, from its first save until it's back with an ID. Its saves go by <see cref="Key"/>
    /// in the meantime, which <see cref="_madeAgain"/> turns into the note's ID once it's made, so the note is made
    /// once, and anything typed while that was on its way is saved to it after.
    /// </summary>
    private sealed class NewNoteSave(long key)
    {
        public long Key { get; } = key;

        /// <summary>Where it goes, while that's still being read from the server.</summary>
        public Task<NotePlacement>? Placing { get; init; }

        /// <summary>Set once its first save is queued, after which that save places it.</summary>
        public bool Sent { get; set; }

        /// <summary>Where it goes, as known when its first save was queued, or null to take it from <see cref="Placing"/>.</summary>
        public NotePlacement? Placement { get; set; }

        /// <summary>The folder it was made in, set by its first save.</summary>
        public long? Folder { get; set; }

        /// <summary>The class title its first save gave it, when no title was typed.</summary>
        public string? Title { get; set; }
    }

    /// <summary>
    /// In server mode, a note that's already there saves off the window's thread, and so does a new one while the
    /// page's work isn't running straight away. Otherwise a new note, or one being placed in a class folder, saves here.
    /// </summary>
    private bool SavesAhead => _notes is Waypoint.Core.Remote.ServerNotesData && (_draft.Id is not null ? _draftPlacement is null : !Work.RunsNow);

    /// <summary>
    /// Once typing stops. In server mode an existing note is saved off the window's thread, and the rows are then
    /// shown from reads made off it too. A new note, or one being placed in a class folder, saves as before.
    /// </summary>
    private void SaveSoon()
    {
        _saveTimer.Stop();
        if (!SavesAhead || !_draft.ShouldSave)
        {
            Save();
            return;
        }

        if (Work.RunsNow && _saves.Count > 0)
        {
            // Without the page's queue, one at a time, so saves reach the server in the order they were typed.
            _saveTimer.Start();
            return;
        }

        SaveAhead();
    }

    /// <summary>
    /// Sends what's typed in the open note, unless that same text is already on its way. It's saved only over the
    /// text the save before it sent, or the editor loaded, and any other version is kept as its own note first. A
    /// note gone from the server meanwhile is made again with what was typed.
    /// </summary>
    private void SaveAhead()
    {
        var plan = _draft.Id is null ? _newNote ??= new NewNoteSave(long.MinValue + ++_newNotes) : null;
        var id = _draft.Id ?? plan!.Key;
        var (title, body) = (_draft.Title, _draft.Body);
        var madeAgain = _madeAgain;
        var last = _saves.LastOrDefault(save => save.Id == id || (madeAgain.TryGetValue(save.Id, out var again) && again == id));
        if (last is not null && last.Title == title && last.Body == body)
        {
            return;
        }

        var expected = last is not null ? last.Body : _draft.ExpectedStoredBody;
        var storedTitle = _draft.ExpectedStoredTitle;
        var notes = _notes;
        var folder = _draftFolder;
        var now = DateTime.Now;
        var maker = _maker;
        if (plan is { Sent: false })
        {
            // Placed by its first save, by what's known now, or by the read of it still on its way.
            plan.Sent = true;
            plan.Placement = _draftPlacement;
            _draftPlacement = null;
            TitleBox.Tag = TitleHint;
        }

        long Saving()
        {
            var target = madeAgain.TryGetValue(id, out var again) ? again : id;
            if (plan is not null && target == id)
            {
                return MakeNew(plan);
            }

            // Typed in a new note while it was being made, with no title of its own, it keeps the class title it was given.
            var saveTitle = plan?.Title is { } given && string.IsNullOrWhiteSpace(title) ? given : title;
            if (NoteSaving.SaveKeepingOther(notes, target, saveTitle, body, expected, now, last is not null ? last.SentTitle : storedTitle))
            {
                return target;
            }

            var made = notes.CreateNote(saveTitle, body, now, plan?.Folder ?? folder);
            madeAgain[id] = made;
            return made;
        }

        // A new note started during class goes in that class's folder, titled with the course and date unless a
        // title was typed, as it would be saved here. The folder is only made once there's a note to put in it.
        long MakeNew(NewNoteSave plan)
        {
            var into = folder;
            var makeTitle = title;
            if ((plan.Placement ?? Placed(plan.Placing)) is { Course: not null } placement && maker is not null)
            {
                into ??= maker.FolderFor(placement, now);
                if (string.IsNullOrWhiteSpace(makeTitle) && placement.Title is { } classTitle)
                {
                    plan.Title = makeTitle = classTitle;
                }
            }

            plan.Folder = into;
            var made = notes.CreateNote(makeTitle, body, now, into);
            madeAgain[plan.Key] = made;
            return made;
        }

        var save = new BackgroundSave(id, title, body, Work.RunsNow ? Task.Run(Saving) : Work.Run(Saving, "Saving a note"), plan);
        _saves.Add(save);
        AfterSave(save);
    }

    /// <summary>Marks the save done once it's back, and shows the rows again. A problem is said the way one from a click is.</summary>
    private async void AfterSave(BackgroundSave save)
    {
        try
        {
            await save.Saving;
        }
        catch (Exception)
        {
            // Thrown again below, on this thread, the way a save made here would.
        }

        if (FinishBackgroundSave(save) != true)
        {
            return;
        }

        if (!Work.RunsNow)
        {
            ShowAfterChanges(force: false);
        }
        else if (_notes is Waypoint.Core.Remote.ServerNotesData server)
        {
            await server.RefreshReadsAsync();
            using (server.UseRecentReads())
            {
                Refresh();
            }
        }
    }

    /// <summary>
    /// Waits for a save sent off the window's thread, if it hasn't finished, and marks what it sent as saved. True if
    /// that's done now, and null if it was done already. A save that failed counts as not saved, so it's sent again.
    /// </summary>
    private bool? FinishBackgroundSave(BackgroundSave save)
    {
        if (!_saves.Remove(save))
        {
            return null;
        }

        var savedTo = save.Saving.GetAwaiter().GetResult();

        // The editor may be on another note by now, such as the copy kept when the server changed this one too. A
        // note made again goes on as the new one, and so does a new note once it's made.
        var plan = _newNote is { } newNote && newNote.Key == save.Id ? newNote : null;
        var made = _draft.Id is null ? plan : null;
        if (_draft.Id == save.Id || _draft.Id == savedTo || made is not null)
        {
            // A new note sent with no title was given its class's title, which it shows unless a title has been typed since.
            var title = save.SentTitle;
            if (made is not null)
            {
                _draftFolder = made.Folder;
                if (title != save.Title && _draft.Title == save.Title)
                {
                    _loading = true;
                    TitleBox.Text = title;
                    _loading = false;
                    _draft.Edit(title, _draft.Body);
                }
            }

            _draft.MarkSaved(savedTo, title, save.Body);
        }

        Bar.Changed();
        if (!_draft.IsDirty)
        {
            SavedLabel.Text = _notes is Waypoint.Core.Remote.ServerNotesData { WaitingToSave: > 0 } ? "Saved on this PC" : "Saved";
        }

        return true;
    }

    /// <summary>Writes the draft without touching the rows, so it's safe in the middle of a click.</summary>
    private bool Flush()
    {
        _saveTimer.Stop();

        // In server mode a note that's already there is sent in the page's order, and this carries straight on.
        if (!Work.RunsNow && SavesAhead)
        {
            if (_draft.ShouldSave)
            {
                SaveAhead();
            }

            return false;
        }

        // Saves still on their way finish first, so this one can't be overtaken by them or mistake one for a change made
        // elsewhere. In the page's order they're already ahead of anything else, so nothing waits for them here.
        var finished = false;
        if (Work.RunsNow)
        {
            foreach (var pending in _saves.ToList())
            {
                finished |= FinishBackgroundSave(pending) == true;
            }
        }

        if (!_draft.ShouldSave)
        {
            return finished;
        }

        var now = DateTime.Now;

        // A note changed elsewhere while this editor held it, such as its .md file edited while typing here or a reply
        // added from Discord, would be lost under this save. The save only goes over the text the editor loaded, and
        // any other version is kept as its own note first, as the file sync does. A note removed while it was being
        // typed in comes back as a new one, so the text isn't lost.
        long id;
        if (_draft.Id is { } existing && NoteSaving.SaveKeepingOther(_notes, existing, _draft.Title, _draft.Body, _draft.ExpectedStoredBody, now, _draft.ExpectedStoredTitle))
        {
            id = existing;
        }
        else
        {
            // A new note started during class goes in that class's folder, titled with the course and date
            // unless a title was typed. The folder is only made once there's a note to put in it.
            var title = _draft.Title;
            if (_draft.Id is null && _draftPlacement is null && _newNote is { Sent: false, Placing.IsCompletedSuccessfully: true } placing)
            {
                // Closing, with where it goes read but not taken up yet.
                _draftPlacement = Placed(placing.Placing) is { Course: not null } read ? read : null;
            }

            if (_draft.Id is null && _draftPlacement is { Course: not null } placement && _maker is { } maker)
            {
                _draftFolder ??= maker.FolderFor(placement, now);
                if (string.IsNullOrWhiteSpace(title) && placement.Title is { } classTitle)
                {
                    title = classTitle;
                }

                _draftPlacement = null;
                TitleBox.Tag = TitleHint;
            }

            id = _notes.CreateNote(title, _draft.Body, now, _draftFolder);
            if (title != _draft.Title)
            {
                _loading = true;
                TitleBox.Text = title;
                _loading = false;
                _draft.Edit(title, _draft.Body);
            }
        }

        _draft.MarkSaved(id);
        Bar.Changed();

        // In server mode with the server away, typing is kept on this PC and saved there once it's back.
        SavedLabel.Text = _notes is Waypoint.Core.Remote.ServerNotesData { WaitingToSave: > 0 } ? "Saved on this PC" : "Saved";
        return true;
    }
}
