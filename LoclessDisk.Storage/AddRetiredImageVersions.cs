using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000900_AddRetiredImageVersions")]
public sealed class AddRetiredImageVersions : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.CreateTable("RetiredVersions", table => new
        {
            Id = table.Column<string>("TEXT")
        }, constraints: table => table.PrimaryKey("PK_RetiredVersions", row => row.Id));

    protected override void Down(MigrationBuilder migration) => migration.DropTable("RetiredVersions");
}
