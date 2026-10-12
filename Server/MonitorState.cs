using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TeeTmeFinder.Server
{
    public class MonitorState
    {
        private readonly ReaderWriterLockSlim _lock = new();
        private readonly List<string> _logs = new();

        public DateTime StartDate
        {
            get { _lock.EnterReadLock(); try { return _startDate; } finally { _lock.ExitReadLock(); } }
            set { _lock.EnterWriteLock(); try { _startDate = value; } finally { _lock.ExitWriteLock(); } }
        }

        public DateTime EndDate
        {
            get { _lock.EnterReadLock(); try { return _endDate; } finally { _lock.ExitReadLock(); } }
            set { _lock.EnterWriteLock(); try { _endDate = value; } finally { _lock.ExitWriteLock(); } }
        }

        public TimeSpan MinTime
        {
            get { _lock.EnterReadLock(); try { return _minTime; } finally { _lock.ExitReadLock(); } }
            set { _lock.EnterWriteLock(); try { _minTime = value; } finally { _lock.ExitWriteLock(); } }
        }

        public TimeSpan MaxTime
        {
            get { _lock.EnterReadLock(); try { return _maxTime; } finally { _lock.ExitReadLock(); } }
            set { _lock.EnterWriteLock(); try { _maxTime = value; } finally { _lock.ExitWriteLock(); } }
        }

        public int RequiredSlots
        {
            get { _lock.EnterReadLock(); try { return _requiredSlots; } finally { _lock.ExitReadLock(); } }
            set { _lock.EnterWriteLock(); try { _requiredSlots = value; } finally { _lock.ExitWriteLock(); } }
        }

        public bool IsActive
        {
            get { _lock.EnterReadLock(); try { return _isActive; } finally { _lock.ExitReadLock(); } }
            set { _lock.EnterWriteLock(); try { _isActive = value; } finally { _lock.ExitWriteLock(); } }
        }

        public string WebhookUrl
        {
            get { _lock.EnterReadLock(); try { return _webhookUrl; } finally { _lock.ExitReadLock(); } }
            set
            {
                _lock.EnterWriteLock();
                try
                {
                    var v = (value ?? string.Empty).Trim();
                    // basic length limit to avoid abuse
                    if (v.Length > 2000) v = v.Substring(0, 2000);
                    _webhookUrl = v;
                }
                finally { _lock.ExitWriteLock(); }
            }
        }

        public IReadOnlyList<string> Logs
        {
            get { _lock.EnterReadLock(); try { return _logs.AsReadOnly(); } finally { _lock.ExitReadLock(); } }
        }

        private DateTime _startDate = DateTime.Today;
        private DateTime _endDate = DateTime.Today.AddDays(7);
        private TimeSpan _minTime = TimeSpan.FromHours(6);
        private TimeSpan _maxTime = TimeSpan.FromHours(22);
        private int _requiredSlots = 3;
        private bool _isActive = false;
        private string _webhookUrl = string.Empty;

        public void AddLog(string msg)
        {
            _lock.EnterWriteLock();
            try
            {
                _logs.Add($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}");
                if (_logs.Count > 200) _logs.RemoveAt(0);
            }
            finally { _lock.ExitWriteLock(); }
        }

        // Persist state to disk as JSON. Caller should handle exceptions as needed.
        public async Task SaveToFileAsync(string path)
        {
            _lock.EnterReadLock();
            try
            {
                var dto = new
                {
                    StartDate = _startDate,
                    EndDate = _endDate,
                    MinTime = _minTime,
                    MaxTime = _maxTime,
                    RequiredSlots = _requiredSlots,
                    IsActive = _isActive,
                    WebhookUrl = _webhookUrl,
                    Logs = _logs
                };
                var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(path, json);
            }
            finally { _lock.ExitReadLock(); }
        }

        // Load state from disk if file exists. Non-matching fields are ignored.
        public async Task LoadFromFileAsync(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var json = await File.ReadAllTextAsync(path);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                if (doc.ValueKind != JsonValueKind.Object) return;

                if (doc.TryGetProperty("StartDate", out var sd) && sd.ValueKind == JsonValueKind.String && DateTime.TryParse(sd.GetString(), out var start)) StartDate = start;
                if (doc.TryGetProperty("EndDate", out var ed) && ed.ValueKind == JsonValueKind.String && DateTime.TryParse(ed.GetString(), out var end)) EndDate = end;
                if (doc.TryGetProperty("MinTime", out var mt) && mt.ValueKind == JsonValueKind.String && TimeSpan.TryParse(mt.GetString(), out var mint)) MinTime = mint;
                if (doc.TryGetProperty("MaxTime", out var xt) && xt.ValueKind == JsonValueKind.String && TimeSpan.TryParse(xt.GetString(), out var maxt)) MaxTime = maxt;
                if (doc.TryGetProperty("RequiredSlots", out var rs) && rs.ValueKind == JsonValueKind.Number && rs.TryGetInt32(out var rsi)) RequiredSlots = rsi;
                if (doc.TryGetProperty("IsActive", out var ia) && ia.ValueKind == JsonValueKind.True) IsActive = true;
                if (doc.TryGetProperty("WebhookUrl", out var wu) && wu.ValueKind == JsonValueKind.String) WebhookUrl = wu.GetString() ?? string.Empty;
                // logs are optional and may be large; ignore restoring logs for now to avoid unbounded growth
            }
            catch
            {
                // ignore corrupt file
            }
        }
    }
}
