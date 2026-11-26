using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000200_AddPxeSettings")]
public sealed class AddPxeSettings : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.CreateTable("PxeSettings", table => new
        {
            Id = table.Column<int>("INTEGER").Annotation("Sqlite:Autoincrement", true),
            Enabled = table.Column<bool>("INTEGER"),
            BindAddress = table.Column<string>("TEXT", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_PxeSettings", row => row.Id));

    protected override void Down(MigrationBuilder migration) => migration.DropTable("PxeSettings");
}
