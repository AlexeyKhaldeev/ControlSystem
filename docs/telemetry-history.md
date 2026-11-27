# v0.2 — Telemetry History

## Хранение

Devices сохраняет последнее принятое состояние для существующих API и Dashboard.
TelemetryHistory сохраняет каждый успешно принятый после обновления snapshot:
bigint identity Id, DeviceId, Hostname, OperatingSystem, AgentVersion,
SampledAtUtc, серверный LastSeenAtUtc, IsSimulated и полный TelemetryJson (text).
JSON содержит CPU (включая null первого sample), RAM, диски и uptime.
Append истории и guarded upsert latest выполняются в одной транзакции. Конкурентный
запрос с меньшим LastSeenAtUtc остаётся в истории, но не откатывает Devices.
Повторно присланный payload создаёт новую запись: протокол не содержит idempotency key.

Миграция 202611270001_TelemetryHistory additive: не меняет Devices и не удаляет данные.
История начинается после обновления; прежнюю историю восстановить из latest нельзя.
Применение: существующая maintenance-команда `--Database:ApplyMigrations=true`.
Обычный старт не изменяет схему. Readiness проверяет обе таблицы.
API/Contracts, HTTPS, авторизация, Agent и Dashboard сохранены.

Индексы B-tree: (DeviceId, LastSeenAtUtc, Id) для диапазона по серверному времени
и стабильного порядка; (LastSeenAtUtc, Id) для retention. PK — Id.
Для чтения использовать ограниченный диапазон и keyset pagination по времени/Id;
публичный endpoint истории в этом инкременте не добавлен.

## Retention

HistoryRetention:Enabled=false по умолчанию. Явное включение разрешает удаление
только истории с LastSeenAtUtc строго меньше serverNow минус RetentionDays.
Devices не удаляется, даже если его последний heartbeat старше cutoff.
Defaults: RetentionDays=30, BatchSize=10000, IntervalMinutes=1.
Диапазоны: 1–3650 дней, 1–10000 строк, 1–1440 минут; проверяются при старте.
Один атомарный DELETE с LIMIT и SKIP LOCKED за цикл, первый цикл при старте.
Ошибки логируются, повтор через интервал; отмена host останавливает worker.
В InMemory история/retention отсутствуют; fallback PostgreSQL не добавлен.

Это eventual cleanup, не точный срок физического удаления. При меньшем batch или
большом interval оператор должен обеспечить пропускную способность выше поступления.
При 10 heartbeat/s поступает 864000 строк/сутки; defaults могут удалять до
14.4 млн строк/сутки при исправной БД. Реальная скорость зависит от I/O и блокировок.
Удаление не возвращает место ОС немедленно: необходимы autovacuum и мониторинг bloat.

## Воспроизведение PostgreSQL-проверок

Задать CONTROLSYSTEM_TEST_POSTGRES строкой подключения к отдельной тестовой БД
и выполнить `dotnet build ControlSystem.sln`, `dotnet test ControlSystem.sln`.
Нужны права CREATE SCHEMA; каждый тест создаёт случайную schema и удаляет только её.
При отсутствии переменной PG-тесты явно skipped; ошибки подключения — failures.
Не использовать production credentials. HTTPS restart проверяет stop/dispose и новый
Kestrel host в одном процессе тестов, а не перезагрузку ОС или падение PostgreSQL.

## Производительность и риски

При 100 ПК/10 секунд: 864000 записей/сутки, 25.92 млн/30 дней.
Есть дополнительный INSERT, транзакция, три индекса и WAL; репрезентативный размер
JSON и скорость диска существенно влияют на объём. Perf smoke измеряет 1000 записей,
p50/p95, размер relation и EXPLAIN диапазона; это не 30-дневный soak и не capacity SLA.
Нужны disk/WAL alerts, autovacuum, backup/restore, контроль lag retention и нагрузочный
прогон на целевом сервере. Partitioning/агрегации отложены до измерений.

## Результаты 27 ноября 2026

Реальный PostgreSQL 17, временный Docker container, localhost:55439, отдельные схемы.
`dotnet build ControlSystem.sln`: exit 0, 0 warnings, 0 errors.
`dotnet test ControlSystem.sln`: exit 0, 46 passed / 0 failed / 0 skipped, 4 s;
из них 8 PostgreSQL integration и 1 новый unit-тест конфигурации.
Проверены additive/repeat migrations, прежние данные, snapshot модели, индексы,
40 конкурентных receipts, оба направления rollback, строгая граница и batch retention,
opt-in worker, stop/dispose/recreate HTTPS host и device/time range.

100 устройств, 1000 последовательных committed writes: 1158.7 ms, 863.0 writes/s,
p50 1.04 ms, p95 1.36 ms. History relation с индексами: 524288 bytes.
EXPLAIN ANALYZE BUFFERS диапазона: Bitmap Index Scan по составному индексу,
5 строк, execution 0.023 ms, planning 0.112 ms, 7 shared hits.
Это измерение небольшого синтетического payload в тёплой локальной БД;
экстраполяция размера на production без проверки объёма JSON/WAL ненадёжна.

Независимый architect review: замечания исправлены; блокирующих замечаний нет.
`git diff --check`: exit 0. Browser E2E (отдельный от solution проект), crash recovery,
перезагрузка ОС, backup/restore и длительный soak в этом прогоне не выполнялись.
Commit/push/merge/deployment не выполнялись. Тестовый контейнер остановлен после прогона.
