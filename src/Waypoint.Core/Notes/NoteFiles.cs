using System.Security.Cryptography;
using System.Text;
using Waypoint.Core.Data;

namespace Waypoint.Core.Notes;

/// <summary>
/// What one sync did. NotesChanged means a note was added, edited, moved, or removed from outside Waypoint. Notice
/// says, in plain words, when files went missing in a way that looked like the folder was emptied or swapped, so
/// the notes were written back instead of deleted.
/// </summary>
public sealed record NoteSyncResult(int Written, int Imported, int Removed, bool NotesChanged, string? Notice = null);

/// <summary>
/// Keeps every note as a .md file in the notes folder, with a folder on disk for each notes folder, so they can
/// be read and edited in other apps. Each sync compares both sides with what they were the last time they
/// matched: a change in Waypoint is written to the file, and a change to the file comes into Waypoint. When both
/// changed, the outside version is kept as its own note so nothing is lost.
/// </summary>
public sealed class NoteFiles(INoteFileStore database, string root)
{
    private const string Extension = ".md";
    private const int LongestName = 120;
    private const string ElsewhereSuffix = Capture.NoteDraft.ElsewhereSuffix;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a note's file must stay gone, seen on more than one pass, before the note is deleted. A sync app or
    /// an editor saving by delete-and-replace can hide a file for a moment, and that's not the user deleting it.
    /// </summary>
    public static readonly TimeSpan MissingGrace = TimeSpan.FromMinutes(1);

    public string Root => root;

    /// <summary>A note title or folder name as it can be written on disk.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        if (clean.Length > LongestName)
        {
            clean = clean[..LongestName];
        }

        clean = clean.TrimEnd('.', ' ');
        if (clean.Length == 0)
        {
            return "Untitled note";
        }

        return ReservedNames.Contains(clean) ? $"{clean}_" : clean;
    }

    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(text)));

    private readonly Lock _gate = new();

    /// <summary>When each note's file was first seen missing, kept from one pass to the next.</summary>
    private readonly Dictionary<long, DateTime> _missingSince = [];

    /// <summary>What the notes and the folder's files were when a pass started that left nothing to try again.</summary>
    private (string Notes, string Files)? _settled;

    /// <summary>
    /// One pass at a time, since two at once could both take the same new file as a new note. A pass isn't made when
    /// nothing changed since one that left nothing to try again and no file waiting out its grace: the notes' change
    /// stamp is the same, and so is every file's name, size, and time. Then nothing is read or hashed.
    /// </summary>
    public NoteSyncResult Sync(DateTime now)
    {
        lock (_gate)
        {
            var notes = database.NotesChangeStamp();
            var files = notes is null ? null : FilesStamp(root);
            if (notes is not null && files is not null && _settled == (notes, files))
            {
                return new NoteSyncResult(0, 0, 0, false);
            }

            // Taken before the pass, so a change made while it runs is found by the next one.
            _settled = null;
            var pass = new Pass(database, root, now, _missingSince);
            var result = pass.Run();
            if (notes is not null && files is not null && !pass.Deferred && _missingSince.Count == 0)
            {
                _settled = (notes, files);
            }

            return result;
        }
    }

    /// <summary>
    /// Every note file a pass would look at, with its size and time, in one hash. Null when the folder isn't there or
    /// can't be listed, so a pass is made and deals with it.
    /// </summary>
    private static string? FilesStamp(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            var lines = new List<string>();
            foreach (var info in new DirectoryInfo(root).EnumerateFiles("*" + Extension, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, info.FullName);
                if (!relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.')))
                {
                    lines.Add($"{relative}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
                }
            }

            lines.Sort(StringComparer.Ordinal);
            return Hash(string.Join("\n", lines));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with no sync running, waiting for one already going to finish, and holding
    /// the next until it's done. A restore runs this way, so no sync acts on notes from before it.
    /// </summary>
    public void WhileHeld(Action action)
    {
        lock (_gate)
        {
            action();
        }
    }

    /// <summary>The folder files are put in when a restore writes over or removes them, under the notes folder.</summary>
    public const string PutAsideFolder = ".Before restore";

    /// <summary>One sync. It reads both sides once at the start and keeps track of the paths it hands out.</summary>
    private sealed class Pass(INoteFileStore database, string root, DateTime now, Dictionary<long, DateTime> missingSince)
    {
        /// <summary>How alike a new file must be to a missing one's last text, from 0 to 1, to be taken as it.</summary>
        private const double SameNote = 0.5;

        private readonly Dictionary<string, FileInfo> _files = new(PathComparer);
        private readonly Dictionary<string, long> _owners = new(PathComparer);
        private readonly List<string> _untracked = [];
        private readonly Dictionary<string, string> _untrackedHashes = new(PathComparer);
        private HashSet<string> _missingHashes = [];
        private Dictionary<long, string> _folderDirs = [];
        private Dictionary<string, long> _dirFolders = new(PathComparer);
        private int _written;
        private int _imported;
        private int _removed;
        private int _writtenBack;
        private bool _changed;

        /// <summary>Something was left for the next pass to try again, such as a file another app had open.</summary>
        public bool Deferred { get; private set; }

        public NoteSyncResult Run()
        {
            // A missing notes folder means it was moved, deleted, or isn't there right now. Its notes are written
            // again rather than taken as deleted.
            var folderWasThere = Directory.Exists(root);
            Directory.CreateDirectory(root);
            Scan();
            LoadFolders();

            // Just restored from a backup: the restored notes win over whatever the folder holds now.
            if (database.PendingNotesRestore() is { } restored)
            {
                return Rewrite(restored);
            }

            var notes = database.GetNoteFileStates();
            var live = notes.Select(note => note.Id).ToHashSet();
            var records = database.GetNoteFileRecords().ToDictionary(record => record.NoteId);

            // Records made against another folder, such as a copy of the data or a folder pointed somewhere else,
            // say nothing about what's in this one. They're started over, so every note is written here and none
            // is taken as deleted.
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var recordedRoot = database.NoteFilesRoot();
            string? notice = null;
            if (recordedRoot is not null && !PathComparer.Equals(recordedRoot, fullRoot) && records.Count > 0)
            {
                database.ClearNoteFileRecords();
                records.Clear();
                missingSince.Clear();
                notice = "The notes folder isn't the one the notes were last kept in, so every note was written to it and none were deleted.";
            }

            if (!PathComparer.Equals(recordedRoot, fullRoot))
            {
                database.SetNoteFilesRoot(fullRoot);
            }

            foreach (var record in records.Values.Where(record => live.Contains(record.NoteId) && _files.ContainsKey(record.Path)))
            {
                _owners[record.Path] = record.NoteId;
            }

            var recorded = records.Values.Select(record => record.Path).ToHashSet(PathComparer);
            _untracked.AddRange(_files.Keys.Where(path => !recorded.Contains(path)).Order(PathComparer));

            // Every file gone, or most of them at once, looks like the folder was emptied, swapped, or isn't there right
            // now, not like the user deleting notes one by one. Those notes are written back instead of deleted.
            var tracked = records.Values.Count(record => live.Contains(record.NoteId));
            var missingRecords = records.Values.Where(record => live.Contains(record.NoteId) && !_files.ContainsKey(record.Path)).ToList();
            var missing = missingRecords.Count;
            var rewrite = missing > 0 && (!folderWasThere || missing == tracked || (tracked >= 2 && missing * 2 > tracked));
            _missingHashes = missingRecords.Select(record => record.FileHash).ToHashSet();

            // A file that's back, or a note that's gone, stops waiting.
            var stillMissing = rewrite ? [] : missingRecords.Select(record => record.NoteId).ToHashSet();
            foreach (var id in missingSince.Keys.Where(id => !stillMissing.Contains(id)).ToList())
            {
                missingSince.Remove(id);
            }

            foreach (var record in records.Values.Where(record => !live.Contains(record.NoteId)))
            {
                ForgetDeleted(record);
            }

            foreach (var note in notes)
            {
                // A file another app has open is left for the next sync.
                Guarded(() => SyncNote(note, records.GetValueOrDefault(note.Id), rewrite));
            }

            foreach (var path in _untracked.ToList())
            {
                Guarded(() => ImportNew(path));
            }

            if (_writtenBack > 0)
            {
                notice ??= _writtenBack == tracked
                    ? "None of the note files were in the notes folder, so the notes were written to it again and none were deleted."
                    : "Most of the note files went missing from the notes folder at once, so they were written back and no notes were deleted.";
            }

            return new NoteSyncResult(_written, _imported, _removed, _changed, notice);
        }

        /// <summary>
        /// Writes every restored note to its file, as if the folder were new. A file in the way that holds something
        /// else, and any file no restored note has, is moved into a dated folder under <see cref="PutAsideFolder"/>,
        /// so nothing in the folder is lost. The sync skips that folder, as it starts with a dot.
        /// </summary>
        private NoteSyncResult Rewrite(string stamp)
        {
            missingSince.Clear();
            database.ClearNoteFileRecords();
            database.SetNoteFilesRoot(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
            var aside = Path.Combine(PutAsideFolder, now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture));
            var claimed = new HashSet<string>(PathComparer);
            var done = true;
            foreach (var note in database.GetNoteFileStates())
            {
                var dir = DirFor(note.FolderId);
                var name = SafeName(note.Title);
                var path = Path.Combine(dir, name + Extension);
                for (var copy = 2; !claimed.Add(path); copy++)
                {
                    path = Path.Combine(dir, $"{name} ({copy}){Extension}");
                }

                done &= Guarded(() =>
                {
                    if (_files.ContainsKey(path))
                    {
                        if (Read(path) is not { } text)
                        {
                            throw new IOException($"{path} can't be read right now.");
                        }

                        if (text == note.Body)
                        {
                            _owners[path] = note.Id;
                            Remember(note, path);
                            return;
                        }

                        PutAside(path, aside);
                    }

                    WriteAt(note, path);
                });
            }

            foreach (var path in _files.Keys.Where(path => !claimed.Contains(path)).ToList())
            {
                done &= Guarded(() => PutAside(path, aside));
            }

            // Anything that couldn't be done is tried again by the next sync, the same way.
            if (done)
            {
                database.NotesRestoreHandled(stamp);
            }

            return new NoteSyncResult(_written, 0, _removed, false);
        }

        private void PutAside(string path, string aside)
        {
            var target = Full(Path.Combine(aside, path));
            for (var copy = 2; File.Exists(target); copy++)
            {
                target = Full(Path.Combine(aside, Path.ChangeExtension(path, null) + $" ({copy}){Extension}"));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(Full(path), target);
            _files.Remove(path);
            _owners.Remove(path);
            _removed++;
            RemoveEmptyFolders(path);
        }

        private void Scan()
        {
            foreach (var full in Directory.EnumerateFiles(root, "*" + Extension, SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, full);

                // Other apps keep their own files in dot folders, such as .obsidian and .trash.
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith('.')))
                {
                    continue;
                }

                _files[relative] = new FileInfo(full);
            }
        }

        /// <summary>Maps each folder to a directory under the notes folder, and back.</summary>
        private void LoadFolders()
        {
            _folderDirs = [];
            _dirFolders = new(PathComparer);
            void Walk(IEnumerable<NoteFolder> folders, string parent)
            {
                var used = new HashSet<string>(PathComparer);
                foreach (var folder in folders)
                {
                    var name = SafeName(folder.Name);
                    if (!used.Add(name))
                    {
                        name = $"{name} ({folder.Id})";
                    }

                    var dir = Path.Combine(parent, name);
                    _folderDirs[folder.Id] = dir;
                    _dirFolders[dir] = folder.Id;
                    Walk(folder.Children, dir);
                }
            }

            Walk(database.GetNoteFolders(), "");
        }

        /// <summary>A note deleted in Waypoint takes its file with it, unless the file was changed since.</summary>
        private void ForgetDeleted(NoteFileRecord record)
        {
            database.RemoveNoteFileRecord(record.NoteId);
            if (!_files.TryGetValue(record.Path, out var info))
            {
                return;
            }

            if (Unchanged(record, info, out _))
            {
                DeleteFile(record.Path);
            }
            else if (!_untracked.Contains(record.Path, PathComparer))
            {
                _untracked.Add(record.Path);
            }
        }

        private void SyncNote(NoteFileState note, NoteFileRecord? record, bool rewrite)
        {
            if (record is null)
            {
                // A file already where the note goes, holding the same text, is the note's own, such as one
                // imported just as the server went away before it was recorded. It's taken rather than copied.
                var path = Path.Combine(DirFor(note.FolderId), SafeName(note.Title) + Extension);
                if (_untracked.Contains(path, PathComparer) && UntrackedHash(path) == Hash(note.Body))
                {
                    _untracked.RemoveAll(other => PathComparer.Equals(other, path));
                    _owners[path] = note.Id;
                    Remember(note, path);
                    return;
                }

                Write(note, null);
                return;
            }

            // A folder renamed or moved in Waypoint changes where the note belongs without changing the note.
            var waypointChanged = record.Title != note.Title || record.FolderId != note.FolderId || record.BodyHash != Hash(note.Body)
                || !PathComparer.Equals(DirFor(note.FolderId), Path.GetDirectoryName(record.Path) ?? "");
            if (_files.TryGetValue(record.Path, out var info))
            {
                if (Unchanged(record, info, out var text))
                {
                    if (waypointChanged)
                    {
                        Write(note, record);
                    }
                    else if (text is not null)
                    {
                        // Touched but not changed, so remember its new size and time and skip reading it next time.
                        database.SetNoteFileRecord(record with { Size = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks });
                    }

                    return;
                }

                if (text is null)
                {
                    return;
                }

                if (!waypointChanged)
                {
                    // Saved only over the text read at the start of this pass, so a save from the editor meanwhile
                    // isn't lost under the file's. Then it was changed on both sides after all.
                    var saved = database.SaveNoteIfUnchanged(note.Id, note.Title, text, note.Body, now);
                    if (saved.Outcome == NoteSaveOutcome.Saved)
                    {
                        Remember(note with { Body = text }, record.Path);
                        _imported++;
                        _changed = true;
                        return;
                    }

                    if (saved.Stored is not { } stored)
                    {
                        // Deleted meanwhile. The next pass finds the file changed since, and keeps it as a new note.
                        return;
                    }

                    note = note with { Title = stored.Title, Body = stored.Body, FolderId = stored.FolderId };
                }

                // Changed on both sides: Waypoint's version keeps the note, the outside one becomes a new note.
                var copy = FreePath(Path.GetDirectoryName(record.Path) ?? "", Path.GetFileNameWithoutExtension(record.Path) + ElsewhereSuffix);
                File.Move(Full(record.Path), Full(copy));
                _files.Remove(record.Path);
                _owners.Remove(record.Path);
                _files[copy] = new FileInfo(Full(copy));
                _untracked.Add(copy);
                Write(note, record);
                return;
            }

            // Gone from where it was. The same text somewhere else means it was renamed or moved.
            if (_untracked.FirstOrDefault(path => UntrackedHash(path) == record.FileHash) is { } moved)
            {
                Adopt(note, record, moved, waypointChanged);
                return;
            }

            // Renamed and edited at once, such as by an editor that saves under a new name.
            if (MostLike(note.Body) is { } edited)
            {
                AdoptEdited(note, record, edited);
                return;
            }

            if (rewrite)
            {
                Write(note, record);
                _writtenBack++;
                return;
            }

            // Gone for one pass may be a sync app or an editor replacing the file, so the note waits for a later
            // pass, a while after, before it's taken as deleted.
            if (!missingSince.TryGetValue(note.Id, out var since))
            {
                missingSince[note.Id] = now;
                return;
            }

            if (now - since < MissingGrace)
            {
                return;
            }

            missingSince.Remove(note.Id);
            database.DeleteNote(note.Id, now);
            database.RemoveNoteFileRecord(note.Id);
            _removed++;
            _changed = true;
        }

        /// <summary>Takes the new name and folder from the moved file. Text changed in Waypoint meanwhile is written there.</summary>
        private void Adopt(NoteFileState note, NoteFileRecord record, string path, bool waypointChanged)
        {
            _untracked.Remove(path);
            _owners[path] = note.Id;
            var title = Path.GetFileNameWithoutExtension(path);
            var folder = FolderFor(Path.GetDirectoryName(path) ?? "");
            var body = SaveTitle(note, title);
            database.MoveNoteToFolder(note.Id, folder);
            var moved = note with { Title = title, Body = body, FolderId = folder };
            if (record.BodyHash != Hash(body))
            {
                WriteAt(moved, path);
            }
            else
            {
                Remember(moved, path);
            }

            _changed = true;
        }

        /// <summary>
        /// The new file most like a missing note's text, if any is alike enough. A file holding exactly what another
        /// missing note had is that note's, so it's left for it.
        /// </summary>
        private string? MostLike(string body)
        {
            string? best = null;
            var bestScore = SameNote;
            foreach (var path in _untracked)
            {
                if (UntrackedHash(path) is not { } hash || _missingHashes.Contains(hash) || Read(path) is not { } text)
                {
                    continue;
                }

                var score = Similarity(body, text);
                if (score >= bestScore)
                {
                    best = path;
                    bestScore = score;
                }
            }

            return best;
        }

        /// <summary>
        /// Takes the new name, folder, and text from the file. If the note was changed in Waypoint too, Waypoint's
        /// version keeps the note and the file's becomes a note of its own, the same as any edit made on both sides.
        /// </summary>
        private void AdoptEdited(NoteFileState note, NoteFileRecord record, string path)
        {
            if (Read(path) is not { } text)
            {
                return;
            }

            _untracked.Remove(path);
            _owners[path] = note.Id;
            var title = Path.GetFileNameWithoutExtension(path);
            var dir = Path.GetDirectoryName(path) ?? "";
            var folder = FolderFor(dir);
            var moved = note with { Title = title, FolderId = folder };

            // Saved only over the text read at the start of this pass, so a save from the editor meanwhile isn't
            // lost under the file's. Then it was changed on both sides after all.
            var saved = record.BodyHash == Hash(note.Body) ? database.SaveNoteIfUnchanged(note.Id, title, text, note.Body, now) : null;
            if (saved?.Outcome == NoteSaveOutcome.Saved)
            {
                database.MoveNoteToFolder(note.Id, folder);
                Remember(moved with { Body = text }, path);
                _imported++;
            }
            else
            {
                moved = moved with { Body = SaveTitle(saved?.Stored is { } stored ? note with { Body = stored.Body } : note, title) };
                database.MoveNoteToFolder(note.Id, folder);
                var copy = FreePath(dir, title + ElsewhereSuffix);
                File.Move(Full(path), Full(copy));
                _files.Remove(path);
                _files[copy] = new FileInfo(Full(copy));
                _untracked.Add(copy);
                WriteAt(moved, path);
            }

            _changed = true;
        }

        /// <summary>
        /// Gives the note a new title and keeps whatever text it has, even one saved from the editor during this pass,
        /// and returns that text.
        /// </summary>
        private string SaveTitle(NoteFileState note, string title)
        {
            var body = note.Body;
            for (var attempt = 0; attempt < NoteSaving.Tries; attempt++)
            {
                var result = database.SaveNoteIfUnchanged(note.Id, title, body, body, now);
                if (result.Outcome != NoteSaveOutcome.Changed || result.Stored is not { } stored)
                {
                    return body;
                }

                body = stored.Body;
            }

            // Still changing each time it's checked, so the title waits for the next pass rather than risk the text.
            return body;
        }

        /// <summary>How much two texts share, by their words, from 0 for nothing to 1 for the same words.</summary>
        private static double Similarity(string a, string b)
        {
            static Dictionary<string, int> Words(string text)
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    counts[word] = counts.GetValueOrDefault(word) + 1;
                }

                return counts;
            }

            var first = Words(a);
            var second = Words(b);
            var shared = first.Sum(pair => Math.Min(pair.Value, second.GetValueOrDefault(pair.Key)));
            var all = first.Values.Sum() + second.Values.Sum() - shared;
            return all == 0 ? 0 : shared / (double)all;
        }

        /// <summary>A file no note owns, made outside Waypoint, becomes a note in the folder it's in.</summary>
        private void ImportNew(string path)
        {
            if (Read(path) is not { } text)
            {
                return;
            }

            var folder = FolderFor(Path.GetDirectoryName(path) ?? "");
            var id = database.CreateNote(Path.GetFileNameWithoutExtension(path), text, now, folder);
            if (database.GetNote(id) is { } note)
            {
                _owners[path] = id;
                Remember(new NoteFileState(id, note.Title, text, note.FolderId), path);
            }

            _imported++;
            _changed = true;
        }

        /// <summary>Writes the note where its title and folder say, moving the file it had if that's somewhere else.</summary>
        private void Write(NoteFileState note, NoteFileRecord? record)
        {
            var dir = DirFor(note.FolderId);
            var name = SafeName(note.Title);
            var keep = record is not null && PathComparer.Equals(Path.GetDirectoryName(record.Path) ?? "", dir)
                && IsNameFor(Path.GetFileNameWithoutExtension(record.Path), name) && IsFreeFor(record.Path, note.Id);
            var path = keep ? record!.Path : FreePath(dir, name, note.Id);
            WriteAt(note, path);

            if (record is not null && !PathComparer.Equals(record.Path, path) && _files.ContainsKey(record.Path))
            {
                DeleteFile(record.Path);
            }
        }

        private string DirFor(long? folderId) => folderId is { } id && _folderDirs.TryGetValue(id, out var dir) ? dir : "";

        private void WriteAt(NoteFileState note, string path)
        {
            var full = Full(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, note.Body, Utf8);
            _files[path] = new FileInfo(full);
            _owners[path] = note.Id;
            Remember(note, path);
            _written++;
        }

        private void Remember(NoteFileState note, string path)
        {
            var info = new FileInfo(Full(path));
            var hash = Hash(note.Body);
            database.SetNoteFileRecord(new NoteFileRecord(note.Id, path, hash, info.Length, info.LastWriteTimeUtc.Ticks, note.Title, note.FolderId, hash));
        }

        /// <summary>"Name" or "Name (2)" and so on, which Waypoint uses when two notes share a title.</summary>
        private static bool IsNameFor(string fileName, string name) =>
            PathComparer.Equals(fileName, name)
            || (fileName.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(')') && int.TryParse(fileName[(name.Length + 2)..^1], out _));

        private string FreePath(string dir, string name, long? noteId = null)
        {
            for (var copy = 1; ; copy++)
            {
                var path = Path.Combine(dir, (copy == 1 ? name : $"{name} ({copy})") + Extension);
                if (IsFreeFor(path, noteId))
                {
                    return path;
                }
            }
        }

        /// <summary>A path is free for a note when no other note has it and nothing else is on disk there.</summary>
        private bool IsFreeFor(string path, long? noteId) =>
            _owners.TryGetValue(path, out var owner) ? owner == noteId : !_files.ContainsKey(path) && !File.Exists(Full(path));

        private bool Guarded(Action sync)
        {
            try
            {
                sync();
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Nothing was recorded for it, so the next sync tries again.
                Deferred = true;
                return false;
            }
        }

        /// <summary>The folder a directory stands for, making any that don't exist yet. The top of the notes folder is Unfiled.</summary>
        private long? FolderFor(string dir)
        {
            if (dir.Length == 0)
            {
                return null;
            }

            if (_dirFolders.TryGetValue(dir, out var known))
            {
                return known;
            }

            var parentDir = Path.GetDirectoryName(dir) ?? "";
            var parent = FolderFor(parentDir);
            if (parentDir.Length > 0 && parent is null)
            {
                return null;
            }

            var made = database.CreateNoteFolder(Path.GetFileName(dir), parent, now);
            if (made.Id == 0)
            {
                return parent;
            }

            LoadFolders();
            _dirFolders[dir] = made.Id;
            return made.Id;
        }

        /// <summary>True when the file still holds what was last synced. Text is set when the file had to be read.</summary>
        private bool Unchanged(NoteFileRecord record, FileInfo info, out string? text)
        {
            text = null;
            if (info.Length == record.Size && info.LastWriteTimeUtc.Ticks == record.ModifiedTicks)
            {
                return true;
            }

            // A file that can't be read right now, such as one another app is saving, waits for the next sync.
            text = Read(record.Path);
            return text is null || Hash(text) == record.FileHash;
        }

        private string? UntrackedHash(string path)
        {
            if (!_untrackedHashes.TryGetValue(path, out var hash) && Read(path) is { } text)
            {
                hash = Hash(text);
                _untrackedHashes[path] = hash;
            }

            return hash;
        }

        private string? Read(string path)
        {
            try
            {
                return File.ReadAllText(Full(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Deferred = true;
                return null;
            }
        }

        private void DeleteFile(string path)
        {
            try
            {
                File.Delete(Full(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }

            _files.Remove(path);
            _owners.Remove(path);
            _removed++;
            RemoveEmptyFolders(path);
        }

        /// <summary>A folder left empty by a move goes too, so renaming a folder doesn't leave the old one behind.</summary>
        private void RemoveEmptyFolders(string path)
        {
            var dir = Path.GetDirectoryName(Full(path));
            while (dir is not null && !PathComparer.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(root))
                && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        private string Full(string path) => Path.Combine(root, path);
    }
}
