using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ByteBench.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameToByteBench : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The product was renamed from GitBench to ByteBench: the built-in system accounts keep their rows (and everything they
            // authored) but take the new names. Each is looked up by its old normalized name, so a database that never had one is untouched.
            foreach (var (oldName, newName, display) in new[]
            {
                ("gitbench-ai", "bytebench-ai", "ByteBench AI"),
                ("gitbench-automation", "bytebench-automation", "ByteBench Automation"),
                ("gitbench-service-desk", "bytebench-service-desk", null),
            })
            {
                var email = newName + "@bytebench.local";
                var displaySql = display == null ? "" : $", \"DisplayName\" = '{display}'";
                migrationBuilder.Sql(
                    $"UPDATE \"AspNetUsers\" SET \"UserName\" = '{newName}', \"NormalizedUserName\" = '{newName.ToUpperInvariant()}', " +
                    $"\"Email\" = '{email}', \"NormalizedEmail\" = '{email.ToUpperInvariant()}'{displaySql} " +
                    $"WHERE \"NormalizedUserName\" = '{oldName.ToUpperInvariant()}' " +
                    $"AND NOT EXISTS (SELECT 1 FROM \"AspNetUsers\" WHERE \"NormalizedUserName\" = '{newName.ToUpperInvariant()}');");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
