using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000300_AddPxeComputers")]
public sealed class AddPxeComputers : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.CreateTable("PxeComputers", table => new
        {
            Mac = table.Column<string>("TEXT"),
            Name = table.Column<string>("TEXT", nullable: true),
            LastSeen = table.Column<long>("INTEGER")
        }, constraints: table => table.PrimaryKey("PK_PxeComputers", row => row.Mac));

    protected override void Down(MigrationBuilder migration) => migration.DropTable("PxeComputers");
}
