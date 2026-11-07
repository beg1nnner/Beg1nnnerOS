using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Beg1nnnerOS.Core;

public sealed class LogItem
{
    public long id { get; set; }
    public double ts { get; set; }
    public string kind { get; set; }
    public string text { get; set; }
}

/// <summary>Журнал действий и ошибок: последние 1000 записей + application.log.</summary>
public sealed class ActionLog
{
    private readonly LinkedList<LogItem> _items = new();
    private readonly Dictionary<string, double> _recent = new();
    private readonly object _lock = new();
    private readonly string _file;
    private long _seq;

    public ActionLog(string profile)
    {
        _file = Path.Combine(profile, "application.log");
        try
        {
            if (File.Exists(_file) && new FileInfo(_file).Length > 2_000_000)
                File.Move(_file, _file + ".1", true);
        }
        catch { }
    }

    public static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public void Add(string kind, string text)
    {
        var now = Now;
        lock (_lock)
        {
            var key = kind + "\u0001" + text;
            if (_recent.TryGetValue(key, out var last) && now - last < 5) return;
            _recent[key] = now;
            if (_recent.Count > 500)
                foreach (var k in _recent.Where(p => now - p.Value >= 5).Select(p => p.Key).ToList())
                    _recent.Remove(k);
            _items.AddLast(new LogItem { id = ++_seq, ts = now, kind = kind, text = text });
            while (_items.Count > 1000) _items.RemoveFirst();
            try
            {
                File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(kind == "error" ? "ERROR" : "INFO")} [{kind}] {text}{Environment.NewLine}");
            }
            catch { }
        }
    }

    public void Error(string text) => Add("error", text);

    public List<LogItem> Tail(int n = 150, string kind = null)
    {
        lock (_lock)
        {
            var items = _items.Where(i => string.IsNullOrEmpty(kind) || i.kind == kind).ToList();
            return items.Skip(Math.Max(0, items.Count - n)).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }
}
