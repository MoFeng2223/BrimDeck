namespace BrimDeck.Core.Agenda;

// What an item added from one notice became (AgendaBook.FromNotice): the notices page shows "已加入" and undoes it with these.
public sealed record AgendaFromNotice(IReadOnlyList<AgendaEvent> Events, IReadOnlyList<AgendaTodo> Todos)
{
    public bool Any => Events.Count > 0 || Todos.Count > 0;
}

// The planner's data and every change to it. Items are records: a change replaces one with an updated copy, keeping its Id.
// Each change that does something raises Changed once (AgendaCenter saves and redraws on it); a change that is refused or
// changes nothing returns null or false and raises nothing. Used on one thread only.
public sealed class AgendaBook
{
    private readonly List<AgendaEvent> _events = [];
    private readonly List<AgendaTodo> _todos = [];
    private readonly Dictionary<DateOnly, string> _notes = [];
    private AgendaData _kept = new();

    public AgendaBook() { }
    public AgendaBook(AgendaData data) { Load(data); }

    public IReadOnlyList<AgendaEvent> Events => _events;
    public IReadOnlyList<AgendaTodo> Todos => _todos;
    public IReadOnlyDictionary<DateOnly, string> Notes => _notes;
    // Counts the changes, so a view can tell whether it is drawn from the current data.
    public int Revision { get; private set; }
    // See AgendaData.RemindedTo. Set by whoever gives the reminders (AgendaCenter); not a change of what the planner shows, so
    // setting it raises nothing: the setter saves it itself.
    public DateTime? RemindedTo { get; set; }
    public event Action? Changed;

    // A copy of the data to save; the items themselves are shared, as they never change.
    public AgendaData Snapshot() => new() { Events = [.. _events], Todos = [.. _todos], Notes = new(_notes), RemindedTo = RemindedTo, Extra = _kept.Extra };

    // Replaces everything with what was read.
    public void Load(AgendaData data)
    {
        _events.Clear(); _todos.Clear(); _notes.Clear();
        Merge(data, mine: false);
    }

    // Takes in what the file held after changes were made here before it could be read: what was made here is kept as it is
    // (the same Id, or a note of the same day), and everything else of the file is added.
    public void MergeLoaded(AgendaData file)
    {
        Merge(file, mine: true);
        Raise();
    }

    private void Merge(AgendaData data, bool mine)
    {
        _kept = data;
        // Merged, the later of the two.
        RemindedTo = mine && RemindedTo is { } here && !(data.RemindedTo > here) ? here : data.RemindedTo;
        var events = _events.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var todos = _todos.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in data.Events) if (events.Add(item.Id) && !todos.Contains(item.Id)) _events.Add(item);
        foreach (var item in data.Todos) if (todos.Add(item.Id) && !events.Contains(item.Id)) _todos.Add(item);
        foreach (var (day, text) in data.Notes) if (!string.IsNullOrWhiteSpace(text) && !(mine && _notes.ContainsKey(day))) _notes[day] = text;
    }

    private void Raise() { Revision++; Changed?.Invoke(); }
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public AgendaEvent? FindEvent(string id) => _events.Find(item => item.Id == id);
    public AgendaTodo? FindTodo(string id) => _todos.Find(item => item.Id == id);
    public string? Note(DateOnly day) => _notes.GetValueOrDefault(day);

    // ---- Events ----

    // An event as the form gives it, checked and put in shape, or null when it cannot be one: no title, a timed event that ends
    // before or as it starts, an all-day event whose last day is before its first. Times are cut to the minute; a timed event
    // without an end lasts an hour; an all-day event keeps only the dates. An event that does not repeat keeps no last day or
    // skipped days; a series keeps its last day only when it is not before its first day, and the skipped days from its first day.
    // A reminder out of range for the kind of event is dropped (AgendaReminders.Normalize).
    public static AgendaEvent? Shape(string id, string title, DateTime start, DateTime? end, bool allDay, string? detail, AgendaSource? source, DateTime created,
        AgendaRepeat repeat = AgendaRepeat.None, DateOnly? until = null, IReadOnlyList<DateOnly>? skipped = null, int? remind = null)
    {
        if (Clean(title) is not { } name) return null;
        DateTime from, to;
        if (allDay)
        {
            from = AgendaTime.Midnight(DateOnly.FromDateTime(start));
            to = AgendaTime.Midnight(DateOnly.FromDateTime(end ?? start));
            if (to < from) return null;
        }
        else
        {
            from = AgendaTime.Minute(start);
            to = end is { } given ? AgendaTime.Minute(given) : from.AddHours(1);
            if (to <= from) return null;
        }
        var first = DateOnly.FromDateTime(from);
        if (!Enum.IsDefined(repeat) || repeat == AgendaRepeat.None) { repeat = AgendaRepeat.None; until = null; skipped = null; }
        if (until < first) until = null;
        IReadOnlyList<DateOnly> skip = skipped is null ? [] : skipped.Any(day => day < first) ? skipped.Where(day => day >= first).ToList() : skipped;
        return new AgendaEvent
        {
            Id = id, Title = name, Start = from, End = to, AllDay = allDay, Detail = Clean(detail), Source = source, Created = AgendaTime.Second(created),
            Repeat = repeat, Until = until, Skipped = skip, Remind = AgendaReminders.Normalize(remind, allDay)
        };
    }

    public AgendaEvent? AddEvent(string title, DateTime start, DateTime? end, bool allDay, DateTime now, string? detail = null, AgendaSource? source = null, AgendaRepeat repeat = AgendaRepeat.None,
        int? remind = null)
    {
        if (Shape(NewId(), title, start, end, allDay, detail, source, now, repeat, remind: remind) is not { } item) return null;
        _events.Add(item); Raise();
        return item;
    }

    // Changes an event's title, times, all-day, detail and, when given, how it repeats (null keeps it); its Id, source and creation
    // stay, and a series keeps its last day and skipped days (see Shape). A series' times are its first time's: every time of it
    // changes. Null when there is no such event or the new values are refused (see Shape); the event is unchanged then.
    // The reminder stays as it was (made to fit an event that became all-day or timed, AgendaReminders.Normalize); the overload
    // with remind sets it.
    public AgendaEvent? UpdateEvent(string id, string title, DateTime start, DateTime? end, bool allDay, string? detail, AgendaRepeat? repeat = null)
        => Update(id, title, start, end, allDay, detail, repeat, null, keepRemind: true);

    // remind: the new reminder, null for none.
    public AgendaEvent? UpdateEvent(string id, string title, DateTime start, DateTime? end, bool allDay, string? detail, AgendaRepeat? repeat, int? remind)
        => Update(id, title, start, end, allDay, detail, repeat, remind, keepRemind: false);

    private AgendaEvent? Update(string id, string title, DateTime start, DateTime? end, bool allDay, string? detail, AgendaRepeat? repeat, int? remind, bool keepRemind)
    {
        int index = _events.FindIndex(item => item.Id == id);
        if (index < 0) return null;
        var old = _events[index];
        if (keepRemind) remind = old.Remind;
        if (Shape(id, title, start, end, allDay, detail, old.Source, old.Created, repeat ?? old.Repeat, old.Until, old.Skipped, remind) is not { } shaped) return null;
        var item = shaped with { Extra = _events[index].Extra };
        if (item == _events[index]) return item;
        _events[index] = item; Raise();
        return item;
    }

    // Deletes the time of a series that starts on day: the day is skipped. A series left without any time is removed whole, as is
    // an event that does not repeat. Returns the series as it is now; null when it was removed or has no time on day.
    public AgendaEvent? SkipOccurrence(string id, DateOnly day)
    {
        int index = _events.FindIndex(item => item.Id == id);
        if (index < 0 || !_events[index].Occurs(day)) return null;
        var item = _events[index];
        if (item.Repeat == AgendaRepeat.None) { RemoveEvent(id); return null; }
        var changed = item with { Skipped = [.. item.Skipped, day] };
        if (changed.Until is { } until && !changed.OccursBefore(until.AddDays(1))) { RemoveEvent(id); return null; }
        _events[index] = changed; Raise();
        return changed;
    }

    // Deletes the times of a series from day on: its last day becomes the day before. A series without a time before day is
    // removed whole, as is an event that does not repeat. Returns the series as it is now; null when it was removed or not there.
    public AgendaEvent? EndBefore(string id, DateOnly day)
    {
        int index = _events.FindIndex(item => item.Id == id);
        if (index < 0) return null;
        var item = _events[index];
        if (item.Repeat == AgendaRepeat.None || !item.OccursBefore(day)) { RemoveEvent(id); return null; }
        var changed = item with { Until = day.AddDays(-1), Skipped = item.Skipped.Where(skip => skip < day).ToList() };
        _events[index] = changed; Raise();
        return changed;
    }

    // The removed event, to put back with Restore, or null when there was none.
    public AgendaEvent? RemoveEvent(string id)
    {
        int index = _events.FindIndex(item => item.Id == id);
        if (index < 0) return null;
        var item = _events[index];
        _events.RemoveAt(index); Raise();
        return item;
    }

    // ---- To-dos ----

    // A to-do of day (null: "长期"), optionally due on a day. Null when the text is blank.
    public AgendaTodo? AddTodo(string text, DateOnly? day, DateTime now, DateOnly? due = null, AgendaSource? source = null)
    {
        if (Clean(text) is not { } words) return null;
        var item = new AgendaTodo { Id = NewId(), Text = words, Day = day, Due = due, Source = source, Created = AgendaTime.Second(now) };
        _todos.Add(item); Raise();
        return item;
    }

    private AgendaTodo? ChangeTodo(string id, Func<AgendaTodo, AgendaTodo> change)
    {
        int index = _todos.FindIndex(item => item.Id == id);
        if (index < 0) return null;
        var item = change(_todos[index]);
        if (item != _todos[index]) { _todos[index] = item; Raise(); }
        return item;
    }

    // Ticks (now is kept as the time it was done) or unticks a to-do. Null when there is no such to-do.
    public AgendaTodo? SetDone(string id, bool done, DateTime now)
        => ChangeTodo(id, item => item.Done == done ? item : item with { Done = done, DoneAt = done ? AgendaTime.Second(now) : null });

    // Null when there is no such to-do or the text is blank.
    public AgendaTodo? SetTodoText(string id, string text)
        => Clean(text) is { } words ? ChangeTodo(id, item => item with { Text = words }) : null;

    public AgendaTodo? SetDue(string id, DateOnly? due) => ChangeTodo(id, item => item with { Due = due });

    // Moves a to-do to another day, or to "长期" with null.
    public AgendaTodo? SetTodoDay(string id, DateOnly? day) => ChangeTodo(id, item => item with { Day = day });

    public AgendaTodo? RemoveTodo(string id)
    {
        int index = _todos.FindIndex(item => item.Id == id);
        if (index < 0) return null;
        var item = _todos[index];
        _todos.RemoveAt(index); Raise();
        return item;
    }

    // "移到今天": the to-dos AgendaQuery.CarriesOver counts (unfinished, of an earlier day, without a due day) now belong to today.
    // Returns how many moved.
    public int MoveToToday(DateOnly today)
    {
        int moved = 0;
        for (int i = 0; i < _todos.Count; i++)
            if (AgendaQuery.CarriesOver(_todos[i], today)) { _todos[i] = _todos[i] with { Day = today }; moved++; }
        if (moved > 0) Raise();
        return moved;
    }

    // ---- Notes ----

    // Sets a day's note; blank text removes it. Whether anything changed.
    public bool SetNote(DateOnly day, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!_notes.Remove(day)) return false;
        }
        else
        {
            if (_notes.TryGetValue(day, out var old) && old == text) return false;
            _notes[day] = text;
        }
        Raise();
        return true;
    }

    // ---- Between to-dos and events ----

    // Gives a to-do a time: it becomes an event with its text as the title, keeping its Id, source and creation, and the to-do
    // is gone. Null (and the to-do stays) when there is no such to-do or the times are refused (see Shape).
    public AgendaEvent? TodoToEvent(string todoId, DateTime start, DateTime? end, bool allDay, string? detail = null, AgendaRepeat repeat = AgendaRepeat.None, int? remind = null)
    {
        int index = _todos.FindIndex(item => item.Id == todoId);
        if (index < 0) return null;
        var todo = _todos[index];
        if (Shape(todo.Id, todo.Text, start, end, allDay, detail, todo.Source, todo.Created, repeat, remind: remind) is not { } item) return null;
        _todos.RemoveAt(index); _events.Add(item); Raise();
        return item;
    }

    // Takes an event back to a to-do of day (null: "长期"), with its title as the text, keeping its Id, source and creation.
    // The event's detail line, how it repeats and its reminder have no place in a to-do and are dropped.
    public AgendaTodo? EventToTodo(string eventId, DateOnly? day)
    {
        int index = _events.FindIndex(item => item.Id == eventId);
        if (index < 0) return null;
        var item = _events[index];
        var todo = new AgendaTodo { Id = item.Id, Text = item.Title, Day = day, Source = item.Source, Created = item.Created };
        _events.RemoveAt(index); _todos.Add(todo); Raise();
        return todo;
    }

    // ---- Putting back ----

    // Puts back an event or to-do removed earlier (an undo), with its Id. False when an item with that Id is there already.
    public bool Restore(AgendaEvent item)
    {
        if (FindEvent(item.Id) is not null || FindTodo(item.Id) is not null) return false;
        _events.Add(item); Raise();
        return true;
    }
    public bool Restore(AgendaTodo item)
    {
        if (FindEvent(item.Id) is not null || FindTodo(item.Id) is not null) return false;
        _todos.Add(item); Raise();
        return true;
    }

    // ---- From notices ----

    public AgendaFromNotice FromNotice(string noticeId)
        => new(_events.Where(item => item.Source?.NoticeId == noticeId).ToList(), _todos.Where(item => item.Source?.NoticeId == noticeId).ToList());

    // "撤销": removes everything added from the notice. Returns how many items went.
    public int RemoveFromNotice(string noticeId)
    {
        int removed = _events.RemoveAll(item => item.Source?.NoticeId == noticeId) + _todos.RemoveAll(item => item.Source?.NoticeId == noticeId);
        if (removed > 0) Raise();
        return removed;
    }

    // ---- Queries on the current data (AgendaQuery) ----

    public IReadOnlyList<AgendaSegment> Segments(DateOnly day) => AgendaQuery.Segments(_events, day);
    public AgendaTodoDay TodosOf(DateOnly day, DateOnly today) => AgendaQuery.Todos(_todos, day, today);
    public AgendaCounts Counts(DateOnly day, DateOnly today) => AgendaQuery.Counts(Snapshot(), day, today);
    public IReadOnlyList<AgendaDayMark> Marks(DateOnly first, int count) => AgendaQuery.Marks(Snapshot(), first, count);
}
