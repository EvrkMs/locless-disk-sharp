using System.Net;
using LoclessDisk.Core;
using LoclessDisk.Server.Boot;
using LoclessDisk.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = ServerPaths.FindContentRoot(),
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
int port = builder.Configuration.GetValue("AdminPort", 8081);
if (port is < 1 or > 65535)
    throw new InvalidOperationException("AdminPort must be between 1 and 65535.");

builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
string databasePath = Path.GetFullPath(builder.Configuration["CatalogDatabase"] ??
    ServerPaths.FindCatalog(builder.Environment.ContentRootPath), builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
string connection = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
builder.Services.AddDbContext<CatalogDb>(options => options.UseSqlite(connection));
builder.Services.AddScoped<IImageCatalog, SqliteImageCatalog>();
builder.Services.AddScoped<IPxeSettingsStore, SqlitePxeSettingsStore>();
builder.Services.AddScoped<IPxeComputers, SqlitePxeComputers>();

builder.Services.AddControllers();
builder.Services.AddHostedService<PxeHostedService>();

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<CatalogDb>().Database.MigrateAsync();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.Run();
