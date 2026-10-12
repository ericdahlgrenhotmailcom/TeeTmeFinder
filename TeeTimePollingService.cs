using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TeeTmeFinder;

public class TeeTimeMonitorOptions
{
    // Template for the target URL, use {date} placeholder if you want the date injected (yyyy-MM-dd)
    public string TargetUrlTemplate { get; set; } = string.Empty;
    // Optional explicit date to query (yyyy-MM-dd). If empty, today's date is used.
    public string? Date { get; set; }
    // Time window filters (HH:mm)
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }
    // Minimum number of bookable links in a single slot to consider it a match
    public int MinBookablePerSlot { get; set; } = 3;
    public int PollIntervalMinSeconds { get; set; } = 200;
    public int PollIntervalMaxSeconds { get; set; } = 400;
    public string? WebhookUrl { get; set; }
}

public class TeeTimePollingService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TeeTimePollingService> _logger;
    private readonly TeeTimeMonitorOptions _options;
    private readonly MonitorState _monitorState;

    // Keep track of seen booking links
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public TeeTimePollingService(IHttpClientFactory httpClientFactory, IOptions<TeeTimeMonitorOptions> options, ILogger<TeeTimePollingService> logger, MonitorState monitorState)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _options = options.Value;
        _monitorState = monitorState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.TargetUrlTemplate))
        {
            _logger.LogWarning("TeeTimeMonitor.TargetUrlTemplate is not configured. Polling service will not run.");
            return;
        }

        var rnd = new Random();
        _logger.LogInformation("TeeTime polling service started. Template: {Template}", _options.TargetUrlTemplate);

        while (!stoppingToken.IsCancellationRequested)
        {
            // If monitor is not active, sleep a bit and continue
            try
            {
                if (!_monitorState.IsActive)
                {
                    _logger.LogInformation("Monitor is inactive. Sleeping for 30s.");
                    _monitorState.AppendLog("Monitor is inactive. Sleeping for 30s.");
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await PollOnce(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while polling tee time API");
            }

            var delaySeconds = rnd.Next(_options.PollIntervalMinSeconds, _options.PollIntervalMaxSeconds + 1);
            _logger.LogInformation("Waiting {Seconds}s before next poll", delaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }

        _logger.LogInformation("TeeTime polling service stopping.");
    }

    private async Task PollOnce(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();

        // Build URL from template and optional date
        var url = BuildTargetUrl();
        using var resp = await client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Received non-success response {StatusCode} from {Url}", resp.StatusCode, url);
            return;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // Find candidate slots: objects that contain URLs and optional time information
        var candidates = FindCandidates(doc.RootElement);

        var filterDateStart = DateOnly.FromDateTime(_monitorState.StartDate);
        var filterDateEnd = DateOnly.FromDateTime(_monitorState.EndDate);
        var filterStart = _monitorState.MinTime;
        var filterEnd = _monitorState.MaxTime;
        var requiredSlots = _monitorState.RequiredSlots;

        var matchingCandidates = candidates.Where(c =>
        {
            if (c.Urls.Count < Math.Max(1, requiredSlots)) return false;

            if (c.SlotTime != null)
            {
                var slotDate = DateOnly.FromDateTime(c.SlotTime.Value);
                if (slotDate < filterDateStart || slotDate > filterDateEnd) return false;

                var t = c.SlotTime.Value.TimeOfDay;
                if (t < filterStart || t > filterEnd) return false;
            }
            else
            {
                // No slot time info; skip when filters are present
                return false;
            }

            return true;
        }).ToList();

        var discoveredNewUrls = new List<string>();
        foreach (var cand in matchingCandidates)
        {
            foreach (var u in cand.Urls)
            {
                if (!_seen.Contains(u))
                {
                    _seen.Add(u);
                    discoveredNewUrls.Add(u);
                }
            }
        }

        if (discoveredNewUrls.Count > 0)
        {
            _logger.LogInformation("Found {Count} new booking links across {Slots} matching slots", discoveredNewUrls.Count, matchingCandidates.Count);
            await NotifyWebhook(discoveredNewUrls, ct);
        }
        else
        {
            _logger.LogInformation("No new booking links found for configured filters");
        }
    }

    private string BuildTargetUrl()
    {
        var template = _options.TargetUrlTemplate ?? string.Empty;
        if (string.IsNullOrWhiteSpace(template)) return _options.TargetUrlTemplate ?? string.Empty;

        if (template.Contains("{date}", StringComparison.OrdinalIgnoreCase))
        {
            var date = ParseDateOption(_options.Date) ?? DateOnly.FromDateTime(DateTime.Now);
            return template.Replace("{date}", date.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase);
        }

        return template;
    }

    private static DateOnly? ParseDateOption(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateOnly.TryParse(s, out var d)) return d;
        if (DateTime.TryParse(s, out var dt)) return DateOnly.FromDateTime(dt);
        return null;
    }

    private static TimeSpan? ParseTimeOption(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (TimeSpan.TryParse(s, out var ts)) return ts;
        if (TimeOnly.TryParse(s, out var to)) return to.ToTimeSpan();
        return null;
    }

    private record Candidate(DateTime? SlotTime, List<string> Urls);

    private List<Candidate> FindCandidates(JsonElement element)
    {
        var list = new List<Candidate>();
        CollectCandidates(element, list);
        return list;
    }

    private void CollectCandidates(JsonElement element, List<Candidate> outList)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                // Scan current object for urls and possible time strings
                var urls = new List<string>();
                DateTime? slotTime = null;

                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString();
                        if (IsLikelyUrl(s)) urls.Add(s!);
                        else if (slotTime == null)
                        {
                            // Try parse as datetime or time
                            if (DateTime.TryParse(s, out var dt)) slotTime = dt;
                            else if (TimeOnly.TryParse(s, out var to))
                            {
                                // If only time present, combine with configured date or today
                                var baseDate = ParseDateOption(_options.Date) ?? DateOnly.FromDateTime(DateTime.Now);
                                slotTime = baseDate.ToDateTime(to);
                            }
                        }
                    }

                    // Recurse into child elements to discover nested urls/times as well
                    CollectCandidates(prop.Value, outList);
                }

                if (urls.Count > 0)
                {
                    outList.Add(new Candidate(slotTime, urls));
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectCandidates(item, outList);
                break;
            case JsonValueKind.String:
                // top-level string - nothing to collect here except handled in parent
                break;
        }
    }

    private static bool IsLikelyUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri))
        {
            // Heuristic: url contains "book" or "booking" or path looks like reservation
            return s.Contains("book", StringComparison.OrdinalIgnoreCase) || s.Contains("booking", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains("/booking", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains("/book", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private async Task NotifyWebhook(List<string> newUrls, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookUrl))
        {
            _logger.LogInformation("New booking links: {Urls}", string.Join(", ", newUrls));
            return;
        }

        var payload = new { discoveredAt = DateTimeOffset.UtcNow, links = newUrls };
        var client = _httpClientFactory.CreateClient();
        try
        {
            var resp = await client.PostAsJsonAsync(_options.WebhookUrl, payload, ct);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("Posted {Count} new links to webhook", newUrls.Count);
            }
            else
            {
                _logger.LogWarning("Failed to post to webhook. Status: {Status}", resp.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while posting to webhook");
        }
    }
}
