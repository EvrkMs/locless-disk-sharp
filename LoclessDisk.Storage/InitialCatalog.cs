using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LoclessDisk.Storage;

[DbContext(typeof(CatalogDb))]
[Migration("20261126000100_InitialCatalog")]
public sealed class InitialCatalog : Migration
{
    protected override void Up(MigrationBuilder migration)
    {
        migration.CreateTable("Settings", table => new
        {
            Id = table.Column<int>("INTEGER").Annotation("Sqlite:Autoincrement", true),
            ImageRoot = table.Column<string>("TEXT")
        }, constraints: table => table.PrimaryKey("PK_Settings", row => row.Id));

        migration.CreateTable("Versions", table => new
        {
            Id = table.Column<string>("TEXT"),
            ImageId = table.Column<string>("TEXT"),
            Name = table.Column<string>("TEXT"),
            Depth = table.Column<int>("INTEGER"),
            Layers = table.Column<string>("TEXT")
        }, constraints: table => table.PrimaryKey("PK_Versions", row => row.Id));
    }

    protected override void Down(MigrationBuilder migration)
    {
        migration.DropTable("Versions");
        migration.DropTable("Settings");
    }
}

[DbContext(typeof(CatalogDb))]
public sealed class CatalogDbModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.0");
        modelBuilder.Entity("LoclessDisk.Storage.PxeComputerRow", entity =>
        {
            entity.Property<string>("Mac").HasColumnType("TEXT");
            entity.Property<string>("Name").HasColumnType("TEXT");
            entity.Property<long>("LastSeen").HasColumnType("INTEGER");
            entity.HasKey("Mac");
            entity.ToTable("PxeComputers");
        });
        modelBuilder.Entity("LoclessDisk.Storage.PxeSettingsRow", entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            entity.Property<bool>("Enabled").HasColumnType("INTEGER");
            entity.Property<string>("BindAddress").HasColumnType("TEXT");
            entity.Property<string>("BootScriptUrl").HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.ToTable("PxeSettings");
        });
        modelBuilder.Entity("LoclessDisk.Storage.SettingsRow", entity =>
        {
            entity.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            entity.Property<string>("ImageRoot").IsRequired().HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.ToTable("Settings");
        });
        modelBuilder.Entity("LoclessDisk.Storage.VersionRow", entity =>
        {
            entity.Property<string>("Id").HasColumnType("TEXT");
            entity.Property<int>("Depth").HasColumnType("INTEGER");
            entity.Property<string>("ImageId").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("Layers").IsRequired().HasColumnType("TEXT");
            entity.Property<string>("Name").IsRequired().HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.ToTable("Versions");
        });
    }
}
