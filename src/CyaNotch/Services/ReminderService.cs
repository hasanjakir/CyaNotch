using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using CyaNotch.Core;

namespace CyaNotch.Services;

public enum Repeat { None, Daily, Weekdays, Weekly, Monthly }

public sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }
    public Repeat Repeat { get; set; }
    public bool Fired { get; set; }

    [JsonIgnore]
    public string DueText
    {
        get
        {
            var day = Due.Date == DateTime.Today ? "Today" :
                      Due.Date == DateTime.Today.AddDays(1) ? "Tomorrow" : Due.ToString("ddd, MMM d");
            var rep = Repeat == Repeat.None ? "" : $" · {Repeat}";
            return $"{day} {Due:t}{rep}";
        }
    }

    [JsonIgnore] public bool Overdue => Due <= DateTime.Now;
}

public sealed class TodoItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>Reminders (with snooze and repeat) and a simple to-do list.</summary>
public sealed class ReminderService
{
    private sealed class Saved
    {
        public List<Reminder> Reminders { get; set; } = new();
        public List<TodoItem> Todos { get; set; } = new();
    }

    private const string FileName = "reminders.json";

    public ObservableCollection<Reminder> Reminders { get; } = new();
    public ObservableCollection<TodoItem> Todos { get; } = new();

    /// <summary>Raised (on the UI thread, from <see cref="Tick"/>) when a reminder comes due.</summary>
    public event Action<Reminder>? Due;

    public void Load()
    {
        var s = Storage.Load<Saved>(FileName);
        if (s == null) return;
        foreach (var r in s.Reminders.OrderBy(r => r.Due)) Reminders.Add(r);
        foreach (var t in s.Todos) Todos.Add(t);
    }

    public void Save() => Storage.Save(FileName, new Saved { Reminders = Reminders.ToList(), Todos = Todos.ToList() });

    public void Tick()
    {
        foreach (var r in Reminders.Where(r => !r.Fired && r.Due <= DateTime.Now).ToList())
        {
            r.Fired = true;
            Save();
            Due?.Invoke(r);
        }
    }

    public Reminder Add(string text, DateTime due, Repeat repeat)
    {
        var r = new Reminder { Text = text.Trim(), Due = due, Repeat = repeat };
        Insert(r);
        return r;
    }

    private void Insert(Reminder r)
    {
        var i = 0;
        while (i < Reminders.Count && Reminders[i].Due <= r.Due) i++;
        Reminders.Insert(i, r);
        Save();
    }

    public void Snooze(Reminder r, TimeSpan by)
    {
        Reminders.Remove(r);
        r.Due = DateTime.Now + by;
        r.Fired = false;
        Insert(r);
    }

    /// <summary>Done: repeating reminders roll forward to their next time; one-offs are removed.</summary>
    public void Complete(Reminder r)
    {
        Reminders.Remove(r);
        if (r.Repeat == Repeat.None)
        {
            Save();
            return;
        }
        var next = r.Due;
        do next = NextOccurrence(next, r.Repeat); while (next <= DateTime.Now);
        r.Due = next;
        r.Fired = false;
        Insert(r);
    }

    public void Delete(Reminder r)
    {
        Reminders.Remove(r);
        Save();
    }

    private static DateTime NextOccurrence(DateTime d, Repeat rep) => rep switch
    {
        Repeat.Daily => d.AddDays(1),
        Repeat.Weekly => d.AddDays(7),
        Repeat.Monthly => d.AddMonths(1),
        Repeat.Weekdays => d.DayOfWeek switch
        {
            DayOfWeek.Friday => d.AddDays(3),
            DayOfWeek.Saturday => d.AddDays(2),
            _ => d.AddDays(1),
        },
        _ => d,
    };

    /// <summary>
    /// Understands quick phrases: "in 10m", "in 2h", "at 5pm", "tomorrow 9am", "17:30".
    /// Returns null if no time was found.
    /// </summary>
    public static DateTime? ParseWhen(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        var now = DateTime.Now;

        if (s.StartsWith("in "))
        {
            var rest = s[3..].Replace(" ", "");
            var num = new string(rest.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            var unit = rest[num.Length..];
            if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return null;
            return unit switch
            {
                "m" or "min" or "mins" or "minute" or "minutes" => now.AddMinutes(n),
                "h" or "hr" or "hrs" or "hour" or "hours" => now.AddHours(n),
                "d" or "day" or "days" => now.AddDays(n),
                _ => null,
            };
        }

        var day = DateTime.Today;
        if (s.StartsWith("tomorrow"))
        {
            day = day.AddDays(1);
            s = s["tomorrow".Length..].Trim();
            if (s.Length == 0) return day.AddHours(9);
        }
        if (s.StartsWith("at ")) s = s[3..].Trim();

        if (DateTime.TryParse(s, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.NoCurrentDateDefault, out var t) ||
            DateTime.TryParseExact(s, new[] { "htt", "h tt", "hh:mmtt", "h:mmtt", "h:mm tt" }, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.NoCurrentDateDefault, out t))
        {
            var result = day + t.TimeOfDay;
            if (result <= now && day == DateTime.Today) result = result.AddDays(1);
            return result;
        }
        return null;
    }

    public void AddTodo(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Todos.Insert(0, new TodoItem { Text = text.Trim() });
        Save();
    }

    public void ToggleTodo(TodoItem t)
    {
        t.Done = !t.Done;
        var i = Todos.IndexOf(t);
        Todos.RemoveAt(i);
        if (t.Done) Todos.Add(t); else Todos.Insert(0, t);
        Save();
    }

    public void DeleteTodo(TodoItem t)
    {
        Todos.Remove(t);
        Save();
    }
}
