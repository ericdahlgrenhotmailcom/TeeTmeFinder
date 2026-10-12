namespace TeeTmeFinder
{
    public class GolfMonitorSettings
    {
        public string BaseUrl { get; set; } = "https://azurewebsites.net";
        public string StartDate { get; set; } = "2026-10-10";
        public string EndDate { get; set; } = "2026-10-11";
        // HH:mm:ss
        public string MinTime { get; set; } = "09:00:00";
        public string MaxTime { get; set; } = "14:00:00";
        public int RequiredSlots { get; set; } = 3;
        public int PollIntervalMinSeconds { get; set; } = 200;
        public int PollIntervalMaxSeconds { get; set; } = 400;
        public string? WebhookUrl { get; set; }
    }
}
