using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MarumbiSom.Hub.Data.Migrations
{
    /// <inheritdoc />
    public partial class Inicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    user_id = table.Column<int>(type: "INTEGER", nullable: true),
                    action = table.Column<string>(type: "TEXT", nullable: false),
                    target = table.Column<string>(type: "TEXT", nullable: true),
                    data_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "offer_plays",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    store_id = table.Column<int>(type: "INTEGER", nullable: false),
                    offer_id = table.Column<int>(type: "INTEGER", nullable: true),
                    placement_id = table.Column<int>(type: "INTEGER", nullable: true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    result = table.Column<string>(type: "TEXT", nullable: true),
                    reason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_plays", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "playlists",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    shuffle = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlists", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "TEXT", nullable: false),
                    value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "stores",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    slug = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    volume = table.Column<int>(type: "INTEGER", nullable: false),
                    bt_device_name = table.Column<string>(type: "TEXT", nullable: true),
                    timezone = table.Column<string>(type: "TEXT", nullable: false),
                    min_offer_gap_s = table.Column<int>(type: "INTEGER", nullable: false),
                    crossfade_s = table.Column<int>(type: "INTEGER", nullable: false),
                    token_hash = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stores", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tracks",
                columns: table => new
                {
                    hash = table.Column<string>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", nullable: false),
                    artist = table.Column<string>(type: "TEXT", nullable: true),
                    duration_ms = table.Column<long>(type: "INTEGER", nullable: false),
                    size_bytes = table.Column<long>(type: "INTEGER", nullable: false),
                    loudness_lufs = table.Column<double>(type: "REAL", nullable: true),
                    original_name = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    deleted_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracks", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    username = table.Column<string>(type: "TEXT", nullable: false),
                    password_hash = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "commands",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    store_id = table.Column<int>(type: "INTEGER", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    args_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    sent_at = table.Column<long>(type: "INTEGER", nullable: true),
                    result = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commands", x => x.id);
                    table.ForeignKey(
                        name: "FK_commands_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "enroll_codes",
                columns: table => new
                {
                    code_hash = table.Column<string>(type: "TEXT", nullable: false),
                    store_id = table.Column<int>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    used_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_enroll_codes", x => x.code_hash);
                    table.ForeignKey(
                        name: "FK_enroll_codes_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    store_id = table.Column<int>(type: "INTEGER", nullable: false),
                    type = table.Column<string>(type: "TEXT", nullable: false),
                    data_json = table.Column<string>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_events_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "play_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    store_id = table.Column<int>(type: "INTEGER", nullable: false),
                    track_hash = table.Column<string>(type: "TEXT", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_play_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_play_history_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "schedules",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    store_id = table.Column<int>(type: "INTEGER", nullable: false),
                    playlist_id = table.Column<int>(type: "INTEGER", nullable: false),
                    days_mask = table.Column<int>(type: "INTEGER", nullable: false),
                    start_time = table.Column<TimeOnly>(type: "TEXT", nullable: false),
                    end_time = table.Column<TimeOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedules", x => x.id);
                    table.ForeignKey(
                        name: "FK_schedules_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_schedules_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_manifests",
                columns: table => new
                {
                    store_id = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    content_hash = table.Column<string>(type: "TEXT", nullable: false),
                    json = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_manifests", x => new { x.store_id, x.version });
                    table.ForeignKey(
                        name: "FK_store_manifests_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_state",
                columns: table => new
                {
                    store_id = table.Column<int>(type: "INTEGER", nullable: false),
                    applied_version = table.Column<long>(type: "INTEGER", nullable: true),
                    pending_version = table.Column<long>(type: "INTEGER", nullable: true),
                    sync_done = table.Column<int>(type: "INTEGER", nullable: true),
                    sync_total = table.Column<int>(type: "INTEGER", nullable: true),
                    now_playing_hash = table.Column<string>(type: "TEXT", nullable: true),
                    position_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    player_state = table.Column<string>(type: "TEXT", nullable: true),
                    bt_state = table.Column<string>(type: "TEXT", nullable: true),
                    volume = table.Column<int>(type: "INTEGER", nullable: true),
                    disk_free_mb = table.Column<long>(type: "INTEGER", nullable: true),
                    agent_version = table.Column<string>(type: "TEXT", nullable: true),
                    uptime_s = table.Column<long>(type: "INTEGER", nullable: true),
                    last_seen_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_state", x => x.store_id);
                    table.ForeignKey(
                        name: "FK_store_state_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "offers",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    name = table.Column<string>(type: "TEXT", nullable: false),
                    audio_hash = table.Column<string>(type: "TEXT", nullable: false),
                    valid_from = table.Column<long>(type: "INTEGER", nullable: false),
                    valid_until = table.Column<long>(type: "INTEGER", nullable: false),
                    duck_level = table.Column<int>(type: "INTEGER", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offers", x => x.id);
                    table.ForeignKey(
                        name: "FK_offers_tracks_audio_hash",
                        column: x => x.audio_hash,
                        principalTable: "tracks",
                        principalColumn: "hash",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_items",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    playlist_id = table.Column<int>(type: "INTEGER", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    track_hash = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_playlist_items_playlists_playlist_id",
                        column: x => x.playlist_id,
                        principalTable: "playlists",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_playlist_items_tracks_track_hash",
                        column: x => x.track_hash,
                        principalTable: "tracks",
                        principalColumn: "hash",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offer_placements",
                columns: table => new
                {
                    id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    offer_id = table.Column<int>(type: "INTEGER", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    mode = table.Column<string>(type: "TEXT", nullable: true),
                    playlist_id = table.Column<int>(type: "INTEGER", nullable: true),
                    playlist_item_id = table.Column<string>(type: "TEXT", nullable: true),
                    offset_ms = table.Column<long>(type: "INTEGER", nullable: true),
                    every_n = table.Column<int>(type: "INTEGER", nullable: true),
                    every_minutes = table.Column<int>(type: "INTEGER", nullable: true),
                    clock_time = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    days_mask = table.Column<int>(type: "INTEGER", nullable: true),
                    playlist_ids_json = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_placements", x => x.id);
                    table.ForeignKey(
                        name: "FK_offer_placements_offers_offer_id",
                        column: x => x.offer_id,
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "offer_stores",
                columns: table => new
                {
                    offer_id = table.Column<int>(type: "INTEGER", nullable: false),
                    store_id = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_stores", x => new { x.offer_id, x.store_id });
                    table.ForeignKey(
                        name: "FK_offer_stores_offers_offer_id",
                        column: x => x.offer_id,
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_offer_stores_stores_store_id",
                        column: x => x.store_id,
                        principalTable: "stores",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_log_created_at",
                table: "audit_log",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_commands_store_id_created_at",
                table: "commands",
                columns: new[] { "store_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_enroll_codes_store_id",
                table: "enroll_codes",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "IX_events_store_id_created_at",
                table: "events",
                columns: new[] { "store_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_placements_offer_id",
                table: "offer_placements",
                column: "offer_id");

            migrationBuilder.CreateIndex(
                name: "IX_offer_plays_offer_id",
                table: "offer_plays",
                column: "offer_id");

            migrationBuilder.CreateIndex(
                name: "IX_offer_plays_store_id_started_at",
                table: "offer_plays",
                columns: new[] { "store_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_offer_stores_store_id",
                table: "offer_stores",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "IX_offers_audio_hash",
                table: "offers",
                column: "audio_hash");

            migrationBuilder.CreateIndex(
                name: "IX_play_history_store_id_started_at",
                table: "play_history",
                columns: new[] { "store_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_items_playlist_id_position",
                table: "playlist_items",
                columns: new[] { "playlist_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_items_track_hash",
                table: "playlist_items",
                column: "track_hash");

            migrationBuilder.CreateIndex(
                name: "IX_schedules_playlist_id",
                table: "schedules",
                column: "playlist_id");

            migrationBuilder.CreateIndex(
                name: "IX_schedules_store_id",
                table: "schedules",
                column: "store_id");

            migrationBuilder.CreateIndex(
                name: "IX_stores_slug",
                table: "stores",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stores_token_hash",
                table: "stores",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracks_kind",
                table: "tracks",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "IX_users_username",
                table: "users",
                column: "username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "commands");

            migrationBuilder.DropTable(
                name: "enroll_codes");

            migrationBuilder.DropTable(
                name: "events");

            migrationBuilder.DropTable(
                name: "offer_placements");

            migrationBuilder.DropTable(
                name: "offer_plays");

            migrationBuilder.DropTable(
                name: "offer_stores");

            migrationBuilder.DropTable(
                name: "play_history");

            migrationBuilder.DropTable(
                name: "playlist_items");

            migrationBuilder.DropTable(
                name: "schedules");

            migrationBuilder.DropTable(
                name: "settings");

            migrationBuilder.DropTable(
                name: "store_manifests");

            migrationBuilder.DropTable(
                name: "store_state");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "offers");

            migrationBuilder.DropTable(
                name: "playlists");

            migrationBuilder.DropTable(
                name: "stores");

            migrationBuilder.DropTable(
                name: "tracks");
        }
    }
}
