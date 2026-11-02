using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PcMonitor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessGpuPercent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "GpuPercent",
                table: "Processes",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GpuPercent",
                table: "Processes");
        }
    }
}
