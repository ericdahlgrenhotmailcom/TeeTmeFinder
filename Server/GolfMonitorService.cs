using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace TeeTmeFinder.Server
{
    public class GolfMonitorService : BackgroundService, IDisposable
    {
        private readonly MonitorState _state;
        private readonly IHttpClientFactory _httpFactory;
        private readonly Microsoft.Extensions.Logging.ILogger<GolfMonitorService> _logger;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _seen = new();
        private readonly object _seenCleanupLock = new();
        private DateTime _lastSeenCleanup = DateTime.MinValue;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastWebhookSent = new();

        private readonly TimeSpan _webhookRateLimit = TimeSpan.FromSeconds(30);

        public GolfMonitorService(MonitorState state, IHttpClientFactory httpFactory, Microsoft.Extensions.Logging.ILogger<GolfMonitorService> logger)
        {
            _state = state;
            _httpFactory = httpFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await RunAsync(stoppingToken);
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var client = _httpFactory.CreateClient("monitor");
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; TeeTmeFinder/1.0)");

            var rnd = new Random();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_state.IsActive)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    var now = DateTime.Now.TimeOfDay;
                    if (now >= TimeSpan.FromHours(1) && now < TimeSpan.FromHours(7))
                    {
                        _state.AddLog("Nighttime blackout: skipping poll.");
                        await Task.Delay(TimeSpan.FromMinutes(5), ct);
                        continue;
                    }

                    // iterate dates
                    var start = _state.StartDate.Date;
                    var end = _state.EndDate.Date;
                    // sanity check ranges to avoid runaway loops
                    if (end < start)
                    {
                        _state.AddLog("EndDate is before StartDate; skipping this cycle.");
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                        continue;
                    }
                    if ((end - start).TotalDays > 30)
                    {
                        _state.AddLog("Date range too large (>30 days); trimming to 30 days.");
                        end = start.AddDays(30);
                    }

                    for (var d = start; d <= end; d = d.AddDays(1))
                    {
                    var url = $"https://prd-sgf-widget-api.azurewebsites.net/slots?date={d:yyyy-MM-dd}";
                    // validate URL length and scheme safety
                    if (url.Length > 2000) { _state.AddLog("Generated URL too long, skipping"); continue; }
                        using var resp = await client.GetAsync(url, ct);
                        if (!resp.IsSuccessStatusCode)
                        {
                            _state.AddLog($"HTTP {resp.StatusCode} for {url}");
                            _logger.LogWarning("HTTP {Status} for {Url}", resp.StatusCode, url);
                            continue;
                        }

                        using var stream = await resp.Content.ReadAsStreamAsync(ct);
                        if (stream == null) { _state.AddLog("Empty response stream."); continue; }
                        var root = await JsonSerializer.DeserializeAsync<SlotsRoot>(stream, cancellationToken: ct);
                        if (root?.Slots == null) continue;

                        foreach (var slot in root.Slots)
                        {
                            if (slot.Availablity == null) continue;
                            if (!slot.Availablity.Bookable) continue;
                            if (slot.Availablity.AvailableSlots < _state.RequiredSlots) continue;

                            // parse time (Slot.Time is a DateTime in the DTO)
                            DateTimeOffset dto;
                            var dt = slot.Time;
                            if (dt.Kind == DateTimeKind.Unspecified)
                            {
                                // assume UTC when unspecified
                                dto = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                            }
                            else
                            {
                                dto = new DateTimeOffset(dt);
                            }
                            var local = dto.ToLocalTime();
                            var t = local.TimeOfDay;
                            if (t < _state.MinTime || t > _state.MaxTime) continue;

                            var slotId = !string.IsNullOrEmpty(slot.Id) ? slot.Id : slot.Time.ToString("o");
                            // guard: ensure slotId not excessively long
                            if (slotId.Length > 2000) slotId = slotId.Substring(0, 2000);
                            // insert into seen cache with timestamp if not present
                            var added = _seen.TryAdd(slotId, DateTime.UtcNow);
                            if (!added)
                            {
                                // already seen
                                continue;
                            }

                            // periodic cleanup of old seen entries (run infrequently)
                            try
                            {
                                if ((DateTime.UtcNow - _lastSeenCleanup) > TimeSpan.FromMinutes(30))
                                {
                                    lock (_seenCleanupLock)
                                    {
                                        if ((DateTime.UtcNow - _lastSeenCleanup) > TimeSpan.FromMinutes(30))
                                        {
                                            var ttl = TimeSpan.FromDays(1);
                                            var keys = _seen.Keys;
                                            foreach (var k in keys)
                                            {
                                                if (_seen.TryGetValue(k, out var ts) && (DateTime.UtcNow - ts) > ttl)
                                                {
                                                    _seen.TryRemove(k, out _);
                                                }
                                            }
                                            _lastSeenCleanup = DateTime.UtcNow;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error during seen cleanup");
                            }

                            var msg = $"MATCH: {d:yyyy-MM-dd} {local:HH:mm} — Open slots: {slot.Availablity.AvailableSlots} (id={slotId})";
                            _state.AddLog(msg);
                            _logger.LogInformation(msg);
                            try
                            {
                                await SendDiscordAlertAsync(d.ToString("yyyy-MM-dd"), local.ToString("HH:mm"), slot.Availablity.AvailableSlots, ct);
                            }
                            catch (Exception ex)
                            {
                                _state.AddLog($"Discord send error: {ex.Message}");
                                _logger.LogWarning(ex, "Error sending discord alert");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _state.AddLog($"Monitor error: {ex.Message}");
                }

                var wait = TimeSpan.FromSeconds(120 + rnd.Next(0, 31));
                await Task.Delay(wait, ct);
            }
        }

        private async Task SendDiscordAlertAsync(string date, string time, int slots, CancellationToken ct)
        {
            var url = _state.WebhookUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                _state.AddLog("Webhook not configured; skipping Discord alert.");
                return;
            }

            // Basic validation: ensure URL appears to be an HTTP(S) webhook and not too long
            if (!(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                _state.AddLog("Webhook URL does not use HTTP/HTTPS; skipping.");
                return;
            }

            if (url.Length > 2000) { _state.AddLog("Webhook URL too long; skipping."); return; }

            var payload = new { content = $"🟢 **LEDIG TID HITTAD!**\n📅 Datum: {date}\n⏰ Tid: {time}\n⛳ Platser kvar: {slots}\n[Boka här](https://ostgk.se)" };

            try
            {
                var client = _httpFactory.CreateClient("monitor");
                var json = JsonSerializer.Serialize(payload);
                using var sc = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await client.PostAsync(url, sc, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _state.AddLog($"Discord webhook returned {resp.StatusCode}");
                }
                else
                {
                    _state.AddLog("Discord webhook sent.");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _state.AddLog($"Discord send error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _seen.Clear();
                _lastWebhookSent.Clear();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Error during dispose cleanup");
            }
        }
    }
}
