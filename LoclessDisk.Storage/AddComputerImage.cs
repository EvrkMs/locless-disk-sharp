using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000600_AddComputerImage")]
public sealed class AddComputerImage : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.AddColumn<string>("ImageId", "PxeComputers", "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migration) =>
        migration.DropColumn("ImageId", "PxeComputers");
}
