using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000700_AddAgentTaskKinds")]
public sealed class AddAgentTaskKinds : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.AddColumn<string>("Kind", "LinkChecks", "TEXT", nullable: false, defaultValue: "agent");
        migration.AddColumn<string>("ImageId", "LinkChecks", "TEXT", nullable: true);
        migration.AddColumn<byte[]>("RecoveryTable", "PxeComputers", "BLOB", nullable: true);
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.DropColumn("Kind", "LinkChecks");
        migration.DropColumn("ImageId", "LinkChecks");
        migration.DropColumn("RecoveryTable", "PxeComputers");
    }
}
