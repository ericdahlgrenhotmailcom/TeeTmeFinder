using System;
using System.Collections.Generic;
using System.Linq;

namespace TeeTmeFinder
{
    public class MonitorState
    {
        private readonly object _lock = new object();
        private readonly LinkedList<string> _logs = new LinkedList<string>();
        private const int MaxLogs = 10;

        public DateTime StartDate { get; set; } = DateTime.Today;
        public DateTime EndDate { get; set; } = DateTime.Today.AddDays(1);
        public TimeSpan MinTime { get; set; } = TimeSpan.FromHours(9);
        public TimeSpan MaxTime { get; set; } = TimeSpan.FromHours(14);
        public int RequiredSlots { get; set; } = 3;
        public bool IsActive { get; set; } = true;

        public IReadOnlyList<string> Logs
        {
            get
            {
                lock (_lock)
                {
                    return _logs.ToList().AsReadOnly();
                }
            }
        }

        public void AppendLog(string message)
        {
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            lock (_lock)
            {
                _logs.AddFirst(entry);
                while (_logs.Count > MaxLogs)
                {
                    _logs.RemoveLast();
                }
            }
        }
    }
}
