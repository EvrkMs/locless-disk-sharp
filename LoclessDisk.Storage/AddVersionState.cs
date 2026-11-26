using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000800_AddVersionState")]
public sealed class AddVersionState : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.AddColumn<bool>("Disabled", "Versions", "INTEGER", nullable: false, defaultValue: false);

    protected override void Down(MigrationBuilder migration) =>
        migration.DropColumn("Disabled", "Versions");
}
