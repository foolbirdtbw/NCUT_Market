-- Hand-run development seed for the two dictionary tables, so GET /api/categories and
-- GET /api/dormitory-areas have something to return. Not a seeding mechanism: there is no
-- HasData call and nothing runs this on startup.
--
-- Timestamps are written as UTC_TIMESTAMP(3) + INTERVAL 8 HOUR rather than NOW(3).
-- This MySQL instance is pinned to UTC (default-time-zone=+00:00 in my.ini), while these columns
-- hold Beijing time as written by AppDbContext.AuditNow. NOW(3) would store a UTC value into a
-- Beijing-time column and every row would read back eight hours early. The explicit conversion is
-- correct no matter which zone the server is configured for.
--
-- created_at / updated_at are NOT NULL with no database default, so a bare INSERT has to supply them.

INSERT INTO categories (name, parent_id, sort_order, status, created_at, updated_at)
VALUES ('宿舍用品', NULL, 10, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR),
       ('电子数码', NULL, 20, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR),
       ('教材书籍', NULL, 30, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR);

INSERT INTO dormitory_areas (name, sort_order, status, created_at, updated_at)
VALUES ('1号楼', 10, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR),
       ('2号楼', 20, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR),
       ('3号楼', 30, 1, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR, UTC_TIMESTAMP(3) + INTERVAL 8 HOUR);
