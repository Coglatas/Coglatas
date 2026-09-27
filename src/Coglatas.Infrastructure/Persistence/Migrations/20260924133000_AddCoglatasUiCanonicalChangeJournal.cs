using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Coglatas.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260924133000_AddCoglatasUiCanonicalChangeJournal")]
public sealed class AddCoglatasUiCanonicalChangeJournal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "coglatas_ui_canonical_revision_heads",
            columns: table => new
            {
                ScopeKey = table.Column<string>(
                    type: "character varying(64)",
                    maxLength: 64,
                    nullable: false),
                TenantId = table.Column<string>(
                    type: "text",
                    nullable: false),
                WorkspaceId = table.Column<string>(
                    type: "text",
                    nullable: false),
                ProjectId = table.Column<string>(
                    type: "text",
                    nullable: true),
                Revision = table.Column<long>(
                    type: "bigint",
                    nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false,
                    defaultValueSql: "CURRENT_TIMESTAMP")
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_coglatas_ui_canonical_revision_heads",
                    item => item.ScopeKey);
                table.CheckConstraint(
                    "CK_coglatas_ui_canonical_revision_heads_Revision",
                    "\"Revision\" >= 0");
            });

        migrationBuilder.CreateTable(
            name: "coglatas_ui_canonical_change_journal",
            columns: table => new
            {
                ScopeKey = table.Column<string>(
                    type: "character varying(64)",
                    maxLength: 64,
                    nullable: false),
                Revision = table.Column<long>(
                    type: "bigint",
                    nullable: false),
                ChangedDomains = table.Column<int>(
                    type: "integer",
                    nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(
                    type: "timestamp with time zone",
                    nullable: false,
                    defaultValueSql: "CURRENT_TIMESTAMP")
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_coglatas_ui_canonical_change_journal",
                    item => new { item.ScopeKey, item.Revision });
                table.ForeignKey(
                    name: "FK_coglatas_ui_canonical_change_journal_revision_heads_ScopeKey",
                    column: item => item.ScopeKey,
                    principalTable: "coglatas_ui_canonical_revision_heads",
                    principalColumn: "ScopeKey",
                    onDelete: ReferentialAction.Cascade);
                table.CheckConstraint(
                    "CK_coglatas_ui_canonical_change_journal_Revision",
                    "\"Revision\" > 0");
                table.CheckConstraint(
                    "CK_coglatas_ui_canonical_change_journal_ChangedDomains",
                    "\"ChangedDomains\" > 0 AND \"ChangedDomains\" <= 31");
            });

        migrationBuilder.CreateIndex(
            name: "IX_coglatas_ui_canonical_revision_heads_TenantId_WorkspaceId_ProjectId",
            table: "coglatas_ui_canonical_revision_heads",
            columns: new[] { "TenantId", "WorkspaceId", "ProjectId" });

        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION coglatas_ui_validate_revision_head_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW."Revision" <> 0 THEN
                    RAISE EXCEPTION
                        'Coglatas UI canonical revision head must be created at revision zero: new=%',
                        NEW."Revision"
                        USING ERRCODE = '23514';
                END IF;

                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER trg_coglatas_ui_revision_head_insert
            BEFORE INSERT
            ON coglatas_ui_canonical_revision_heads
            FOR EACH ROW
            EXECUTE FUNCTION coglatas_ui_validate_revision_head_insert();

            CREATE OR REPLACE FUNCTION coglatas_ui_enforce_revision_head_step()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                IF NEW."ScopeKey" IS DISTINCT FROM OLD."ScopeKey"
                   OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                   OR NEW."WorkspaceId" IS DISTINCT FROM OLD."WorkspaceId"
                   OR NEW."ProjectId" IS DISTINCT FROM OLD."ProjectId" THEN
                    RAISE EXCEPTION
                        'Coglatas UI canonical revision-head scope identity is immutable'
                        USING ERRCODE = '23514';
                END IF;

                IF NEW."Revision" <> OLD."Revision" + 1 THEN
                    RAISE EXCEPTION
                        'Coglatas UI canonical revision head must advance by exactly one: old=%, new=%',
                        OLD."Revision",
                        NEW."Revision"
                        USING ERRCODE = '23514';
                END IF;

                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER trg_coglatas_ui_revision_head_step
            BEFORE UPDATE
            ON coglatas_ui_canonical_revision_heads
            FOR EACH ROW
            EXECUTE FUNCTION coglatas_ui_enforce_revision_head_step();

            CREATE OR REPLACE FUNCTION coglatas_ui_validate_journal_insert()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                head_revision bigint;
            BEGIN
                SELECT "Revision"
                INTO head_revision
                FROM coglatas_ui_canonical_revision_heads
                WHERE "ScopeKey" = NEW."ScopeKey";

                IF head_revision IS NULL OR NEW."Revision" <> head_revision THEN
                    RAISE EXCEPTION
                        'Coglatas UI canonical journal revision must equal the locked revision head: head=%, journal=%',
                        head_revision,
                        NEW."Revision"
                        USING ERRCODE = '23514';
                END IF;

                RETURN NEW;
            END;
            $$;

            CREATE TRIGGER trg_coglatas_ui_journal_insert_matches_head
            BEFORE INSERT
            ON coglatas_ui_canonical_change_journal
            FOR EACH ROW
            EXECUTE FUNCTION coglatas_ui_validate_journal_insert();

            CREATE OR REPLACE FUNCTION coglatas_ui_reject_journal_rewrite()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION
                    'Coglatas UI canonical change journal is append-only'
                    USING ERRCODE = '23514';
            END;
            $$;

            CREATE TRIGGER trg_coglatas_ui_journal_append_only
            BEFORE UPDATE OR DELETE
            ON coglatas_ui_canonical_change_journal
            FOR EACH ROW
            EXECUTE FUNCTION coglatas_ui_reject_journal_rewrite();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "coglatas_ui_canonical_change_journal");
        migrationBuilder.DropTable(name: "coglatas_ui_canonical_revision_heads");

        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS coglatas_ui_reject_journal_rewrite();
            DROP FUNCTION IF EXISTS coglatas_ui_validate_journal_insert();
            DROP FUNCTION IF EXISTS coglatas_ui_enforce_revision_head_step();
            DROP FUNCTION IF EXISTS coglatas_ui_validate_revision_head_insert();
            """);
    }
}
