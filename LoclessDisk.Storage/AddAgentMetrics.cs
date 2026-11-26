using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126001100_AddAgentMetrics")]
public sealed class AddAgentMetrics : Migration
{
    protected override void Up(MigrationBuilder migration) =>
        migration.AddColumn<string>("Metrics", "LinkChecks", "TEXT", nullable: true);

    protected override void Down(MigrationBuilder migration) =>
        migration.DropColumn("Metrics", "LinkChecks");
}
