using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126001200_AddPxeExitMode")]
public sealed class AddPxeExitMode : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.AddColumn<string>("ExitMode", "PxeSettings", "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migration) =>
        migration.DropColumn("ExitMode", "PxeSettings");
}
