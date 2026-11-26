using LoclessDisk.Core;
using LoclessDisk.Server.Boot;
using Microsoft.AspNetCore.Mvc;

namespace LoclessDisk.Server.Controllers;

[ApiController]
[Route("api/settings/pxe")]
public sealed class PxeSettingsController(IPxeSettingsStore store, IConfiguration configuration,
    IHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var defaults = configuration.GetSection("Pxe").Get<PxeOptions>() ?? new();
        var settings = await store.ReadAsync(ct) ?? new(defaults.Enabled, defaults.BindAddress);
        return Ok(new { settings.Enabled, settings.BindAddress, interfaces = PxeInterfaces.List() });
    }

    [HttpPut]
    public async Task<IActionResult> Put(PxeSettings settings, CancellationToken ct)
    {
        if (settings.Enabled && settings.BindAddress is null ||
            settings.BindAddress is not null && !PxeInterfaces.List().Any(i => i.Address == settings.BindAddress))
            return Problem("Выберите IPv4 активного сетевого интерфейса.", statusCode: 400);

        if (settings.Enabled)
        {
            var options = configuration.GetSection("Pxe").Get<PxeOptions>() ?? new();
            try { PxeHostedService.ValidateBoot(options, environment.ContentRootPath); }
            catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException)
            {
                return Problem(error.Message, statusCode: 400);
            }
        }

        await store.SaveAsync(settings, ct);
        return Ok(settings);
    }
}
