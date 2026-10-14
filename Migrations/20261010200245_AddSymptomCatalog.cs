using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MoodTracker.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSymptomCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "Factors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfigJson",
                table: "Factors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InputType",
                table: "Factors",
                type: "TEXT",
                nullable: false,
                defaultValue: "Number");

            migrationBuilder.AddColumn<string>(
                name: "Polarity",
                table: "Factors",
                type: "TEXT",
                nullable: false,
                defaultValue: "Neutral");

            migrationBuilder.AddColumn<string>(
                name: "SafetyKind",
                table: "Factors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SymptomItemId",
                table: "Factors",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Conditions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conditions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SymptomItems",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    InputType = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    Polarity = table.Column<string>(type: "TEXT", nullable: false),
                    Pole = table.Column<string>(type: "TEXT", nullable: true),
                    SafetyKind = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    RelatedIds = table.Column<string>(type: "TEXT", nullable: true),
                    CrossRefNote = table.Column<string>(type: "TEXT", nullable: true),
                    SourceNote = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymptomItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SymptomItemConditions",
                columns: table => new
                {
                    SymptomItemId = table.Column<string>(type: "TEXT", nullable: false),
                    ConditionId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SymptomItemConditions", x => new { x.SymptomItemId, x.ConditionId });
                    table.ForeignKey(
                        name: "FK_SymptomItemConditions_Conditions_ConditionId",
                        column: x => x.ConditionId,
                        principalTable: "Conditions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SymptomItemConditions_SymptomItems_SymptomItemId",
                        column: x => x.SymptomItemId,
                        principalTable: "SymptomItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Factors_SymptomItemId",
                table: "Factors",
                column: "SymptomItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SymptomItemConditions_ConditionId",
                table: "SymptomItemConditions",
                column: "ConditionId");

            migrationBuilder.CreateIndex(
                name: "IX_SymptomItems_Category",
                table: "SymptomItems",
                column: "Category");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SymptomItemConditions");

            migrationBuilder.DropTable(
                name: "Conditions");

            migrationBuilder.DropTable(
                name: "SymptomItems");

            migrationBuilder.DropIndex(
                name: "IX_Factors_SymptomItemId",
                table: "Factors");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Factors");

            migrationBuilder.DropColumn(
                name: "ConfigJson",
                table: "Factors");

            migrationBuilder.DropColumn(
                name: "InputType",
                table: "Factors");

            migrationBuilder.DropColumn(
                name: "Polarity",
                table: "Factors");

            migrationBuilder.DropColumn(
                name: "SafetyKind",
                table: "Factors");

            migrationBuilder.DropColumn(
                name: "SymptomItemId",
                table: "Factors");
        }
    }
}

