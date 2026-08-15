using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Imperial2030.Server.Migrations.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class AddGameIsPaused : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM sys.columns WHERE name = 'IsPaused' AND object_id = OBJECT_ID('Games'))
                    ALTER TABLE Games ADD IsPaused bit NOT NULL CONSTRAINT DF_Games_IsPaused DEFAULT 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT * FROM sys.columns WHERE name = 'IsPaused' AND object_id = OBJECT_ID('Games'))
                BEGIN
                    DECLARE @ConstraintName nvarchar(200);
                    SELECT @ConstraintName = d.name
                    FROM sys.default_constraints d
                    JOIN sys.columns c ON d.parent_column_id = c.column_id AND d.parent_object_id = c.object_id
                    WHERE d.parent_object_id = OBJECT_ID('Games') AND c.name = 'IsPaused';
                    IF @ConstraintName IS NOT NULL
                        EXEC('ALTER TABLE Games DROP CONSTRAINT ' + @ConstraintName);
                    ALTER TABLE Games DROP COLUMN IsPaused;
                END
            ");
        }
    }
}
