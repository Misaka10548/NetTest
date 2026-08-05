using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetTest.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProbeRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PlanId = table.Column<string>(type: "TEXT", nullable: true),
                    PlanNameSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    TriggerKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationRevision = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CancellationReason = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProbeRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProbeExecutions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    ProbeId = table.Column<string>(type: "TEXT", nullable: true),
                    ProbeNameSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    ProbeType = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupIdSnapshot = table.Column<string>(type: "TEXT", nullable: true),
                    PlanId = table.Column<string>(type: "TEXT", nullable: true),
                    TriggerKind = table.Column<int>(type: "INTEGER", nullable: false),
                    AddressFamily = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedAddress = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigurationSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigurationSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    CancellationReason = table.Column<int>(type: "INTEGER", nullable: false),
                    PrimaryLatencyMs = table.Column<long>(type: "INTEGER", nullable: true),
                    MetricsSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    MetricsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    StartedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProbeExecutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProbeExecutions_ProbeRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ProbeRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProbeExecutions_PlanId_CompletedAtUtc",
                table: "ProbeExecutions",
                columns: new[] { "PlanId", "CompletedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ProbeExecutions_ProbeId_CompletedAtUtc",
                table: "ProbeExecutions",
                columns: new[] { "ProbeId", "CompletedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ProbeExecutions_RunId",
                table: "ProbeExecutions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ProbeExecutions_Status_CompletedAtUtc",
                table: "ProbeExecutions",
                columns: new[] { "Status", "CompletedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ProbeExecutions_TriggerKind_CompletedAtUtc",
                table: "ProbeExecutions",
                columns: new[] { "TriggerKind", "CompletedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ProbeRuns_PlanId_StartedAtUtc",
                table: "ProbeRuns",
                columns: new[] { "PlanId", "StartedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ProbeRuns_Status_StartedAtUtc",
                table: "ProbeRuns",
                columns: new[] { "Status", "StartedAtUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ProbeRuns_TriggerKind_StartedAtUtc",
                table: "ProbeRuns",
                columns: new[] { "TriggerKind", "StartedAtUtc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProbeExecutions");

            migrationBuilder.DropTable(
                name: "ProbeRuns");
        }
    }
}
