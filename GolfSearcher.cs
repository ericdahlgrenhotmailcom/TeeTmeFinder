using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TeeTmeFinder
{
    // Root DTO matching: { "slots": [ ... ] }
    public class SlotsRoot
    {
        [JsonPropertyName("slots")]
        public List<Slot> Slots { get; set; } = new List<Slot>();
    }

    public class Slot
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("flexColor")]
        public string FlexColor { get; set; } = string.Empty;

        // server-provided DateTime for the slot
        [JsonPropertyName("time")]
        public DateTime Time { get; set; }

        // Note: JSON key is spelled "availablity" (with an extra 'a' in the middle)
        [JsonPropertyName("availablity")]
        public Availability? Availablity { get; set; }
    }

    public class Availability
    {
        [JsonPropertyName("bookable")]
        public bool Bookable { get; set; }

        [JsonPropertyName("availableSlots")]
        public int AvailableSlots { get; set; }
    }

    public static class GolfSearcher
    {
        // Keep track of active alerts (slotId -> slot date) to avoid duplicate notifications and allow housekeeping.
        private static readonly ConcurrentDictionary<string, DateOnly> ActiveAlerts = new();

        // Send a webhook notification (e.g., Discord-compatible) with a JSON payload { "content": "message" }.
        public static async Task SendWebhookNotificationAsync(string webhookUrl, string message)
        {
            if (string.IsNullOrWhiteSpace(webhookUrl)) return;

            try
            {
                using var client = new HttpClient();
                var payload = JsonSerializer.Serialize(new { content = message });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var resp = await client.PostAsync(webhookUrl, content);
                if (!resp.IsSuccessStatusCode)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: webhook POST returned {resp.StatusCode}");
                    Console.ResetColor();
                }
            }
            catch (HttpRequestException hre)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: webhook POST failed: {hre.Message}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning: webhook POST unexpected error: {ex.Message}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Searches the provided baseUrl for available golf times between startDate and endDate (inclusive).
        /// The method appends a date query parameter to the URL and sends browser-like headers.
        /// Matches are printed to the console in green when availability >= requiredSlots and time is inside the window.
        /// Returns the number of slots that were examined during the scan.
        /// </summary>
        public static async Task<int> SearchGolfTimesAsync(DateOnly startDate, DateOnly endDate, TimeSpan minTime, TimeSpan maxTime, int requiredSlots, string baseUrl = "https://azurewebsites.net", string? webhookUrl = null)
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            // Add common browser-like headers
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");

            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            };

            var checkedSlots = 0;

            for (var d = startDate; d <= endDate; d = d.AddDays(1))
            {
                var separator = baseUrl.Contains('?') ? '&' : '?';
                var url = $"{baseUrl}{separator}date={d:yyyy-MM-dd}";

                try
                {
                    using var resp = await client.GetAsync(url);
                    if (!resp.IsSuccessStatusCode)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Warning: {resp.StatusCode} when fetching {url}");
                        Console.ResetColor();
                        continue;
                    }

                    using var stream = await resp.Content.ReadAsStreamAsync();

                    SlotsRoot? root = null;
                    try
                    {
                        root = await JsonSerializer.DeserializeAsync<SlotsRoot>(stream, options);
                    }
                    catch (JsonException jex)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Warning: failed to parse JSON for {url}: {jex.Message}");
                        Console.ResetColor();
                        continue;
                    }

                    if (root == null)
                    {
                        Console.WriteLine($"No data returned for {d:yyyy-MM-dd} ({url})");
                        continue;
                    }

                    if (root.Slots == null || root.Slots.Count == 0)
                    {
                        Console.WriteLine($"No slots for {d:yyyy-MM-dd} (closed or empty response)");
                        continue;
                    }

                    foreach (var slot in root.Slots)
                    {
                        try
                        {
                            if (slot == null) continue;
                            checkedSlots++;

                            // Time zone safety: interpret DateTime values with care. If Kind is Unspecified assume UTC.
                            var dt = slot.Time;
                            DateTimeOffset dto;
                            if (dt.Kind == DateTimeKind.Unspecified)
                            {
                                // assume server provides UTC timestamps when kind is unspecified
                                dto = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
                            }
                            else
                            {
                                dto = new DateTimeOffset(dt);
                            }

                            var local = dto.ToLocalTime();
                            var localTimeOfDay = local.TimeOfDay;

                            // Check time window and required slots
                            if (localTimeOfDay >= minTime && localTimeOfDay <= maxTime)
                            {
                                if (slot.Availablity != null && slot.Availablity.Bookable && slot.Availablity.AvailableSlots >= requiredSlots)
                                {
                                    // Avoid duplicate alerts: check active alerts cache
                                    var slotId = !string.IsNullOrWhiteSpace(slot.Id) ? slot.Id : Guid.NewGuid().ToString();
                                    var added = ActiveAlerts.TryAdd(slotId, d);
                                    if (added)
                                    {
                                        Console.ForegroundColor = ConsoleColor.Green;
                                        Console.WriteLine($"Available: {d:yyyy-MM-dd} {local:HH:mm} — Open slots: {slot.Availablity.AvailableSlots} (id={slotId})");
                                        Console.ResetColor();

                                        // Send webhook notification if configured
                                        if (!string.IsNullOrWhiteSpace(webhookUrl))
                                        {
                                            var msg = $"Available: {d:yyyy-MM-dd} {local:HH:mm} — Open slots: {slot.Availablity.AvailableSlots} (id={slotId})";
                                            // Await to ensure delivery; exceptions are handled inside
                                            await SendWebhookNotificationAsync(webhookUrl, msg);
                                        }
                                    }
                                    else
                                    {
                                        // Already alerted for this slot id; skip
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"Error processing slot {slot?.Id}: {ex.Message}");
                            Console.ResetColor();
                        }
                    }
                }
                catch (HttpRequestException hrex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: network error when fetching {url}: {hrex.Message}");
                    Console.ResetColor();
                    continue;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Request failed for {url}: {ex.Message}");
                    Console.ResetColor();
                    continue;
                }
            }

            // Cache housekeeping: remove any active alerts that refer to dates in the past
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                var keysToRemove = ActiveAlerts.Where(kv => kv.Value < today).Select(kv => kv.Key).ToList();
                foreach (var key in keysToRemove)
                {
                    ActiveAlerts.TryRemove(key, out _);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Warning during cache housekeeping: {ex.Message}");
                Console.ResetColor();
            }

            return checkedSlots;
        }
    }
}
