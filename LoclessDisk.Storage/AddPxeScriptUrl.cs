using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000400_AddPxeScriptUrl")]
public sealed class AddPxeScriptUrl : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.AddColumn<string>("BootScriptUrl", "PxeSettings", "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migration) =>
        migration.DropColumn("BootScriptUrl", "PxeSettings");
}
