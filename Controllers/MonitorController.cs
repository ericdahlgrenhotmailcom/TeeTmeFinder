using Microsoft.AspNetCore.Mvc;
using TeeTmeFinder.Server;

[ApiController]
[Route("api/[controller]")]
public class MonitorController : ControllerBase
{
    private readonly MonitorState _state;

    public MonitorController(MonitorState state)
    {
        _state = state;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            isActive = _state.IsActive,
            startDate = _state.StartDate,
            endDate = _state.EndDate,
            minTime = _state.MinTime,
            maxTime = _state.MaxTime,
            requiredSlots = _state.RequiredSlots,
            webhookUrl = _state.WebhookUrl,
            logs = _state.Logs
        });
    }

    [HttpPost("start")]
    public IActionResult Start([FromBody] StartRequest dto)
    {
        if (dto == null) return BadRequest();
        _state.StartDate = dto.StartDate;
        _state.EndDate = dto.EndDate;
        _state.MinTime = dto.MinTime;
        _state.MaxTime = dto.MaxTime;
        _state.RequiredSlots = dto.RequiredSlots;
        _state.WebhookUrl = dto.WebhookUrl ?? string.Empty;
        _state.IsActive = true;
        _state.AddLog("Monitor started via controller.");
        // persist settings
        _ = _state.SaveToFileAsync("monitorstate.json");
        return Ok();
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _state.IsActive = false;
        _state.AddLog("Monitor stopped via controller.");
        // persist settings
        _ = _state.SaveToFileAsync("monitorstate.json");
        return Ok();
    }
}

public record StartRequest(DateTime StartDate, DateTime EndDate, TimeSpan MinTime, TimeSpan MaxTime, int RequiredSlots, string? WebhookUrl);
