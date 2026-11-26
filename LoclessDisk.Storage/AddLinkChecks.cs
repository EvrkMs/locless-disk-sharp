using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000500_AddLinkChecks")]
public sealed class AddLinkChecks : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.CreateTable("LinkChecks", table => new
        {
            Id = table.Column<long>("INTEGER").Annotation("Sqlite:Autoincrement", true),
            Mac = table.Column<string>("TEXT"),
            Status = table.Column<string>("TEXT"),
            Percent = table.Column<int>("INTEGER"),
            Stage = table.Column<string>("TEXT"),
            Message = table.Column<string>("TEXT"),
            CreatedAt = table.Column<long>("INTEGER"),
            FinishedAt = table.Column<long>("INTEGER", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_LinkChecks", row => row.Id));
        migration.CreateIndex("IX_LinkChecks_Mac", "LinkChecks", "Mac", unique: true,
            filter: "\"FinishedAt\" IS NULL");
    }

    protected override void Down(MigrationBuilder migration) => migration.DropTable("LinkChecks");
}
