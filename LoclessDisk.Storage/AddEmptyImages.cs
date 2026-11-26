using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126001000_AddEmptyImages")]
public sealed class AddEmptyImages : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.CreateTable("EmptyImages", table => new
        {
            Id = table.Column<string>("TEXT"),
            Name = table.Column<string>("TEXT"),
            Directory = table.Column<string>("TEXT")
        }, constraints: table => table.PrimaryKey("PK_EmptyImages", row => row.Id));

    protected override void Down(MigrationBuilder migration) => migration.DropTable("EmptyImages");
}
