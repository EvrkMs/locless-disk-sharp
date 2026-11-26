using LoclessDisk.Server.Scenarios;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/scenarios")]
public sealed class ScenariosController(BenchmarkScenario scenario) : ControllerBase
{
    [HttpGet]
    public IActionResult Status() => Ok(new { scenario.Enabled });

    [HttpGet("events")]
    public IResult Events(CancellationToken ct) => scenario.Enabled
        ? TypedResults.ServerSentEvents(scenario.Stream(ct), "scenarios") : Results.NotFound();

    [HttpPost]
    public async Task<IActionResult> Start(StartInput input, CancellationToken ct)
    {
        if (!scenario.Enabled)
            return NotFound();
        if (!input.Confirmed)
            return Problem("Подтвердите запись на диск тестового ПК.", statusCode: 400);
        string mac = input.Mac?.ToUpperInvariant() ?? "";
        return await scenario.StartRunAsync(mac, ct) is { } error ? Problem(error, statusCode: 409) : NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Stop(CancellationToken ct) =>
        scenario.Enabled && await scenario.StopRunAsync(ct) ? NoContent() : NotFound();

    public sealed record StartInput(string? Mac, bool Confirmed);
}
