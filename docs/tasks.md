# План работ и делегирование

Обновлено: 22 ноября 2026. Проверки и ограничения среды — `verification.md`.

## Выполнено в стартовом инкременте

- [x] Изучен и удалён старый Python/FastAPI scaffold; исходники сохранены в `/tmp/controlsystem-legacy-20261122.tar.gz`.
- [x] Проверены SDK/Git/Docker/PostgreSQL; создана solution и четыре проекта .NET 10.
- [x] Определены границы, ADR, требования, API и модель данных.
- [x] Heartbeat, device list, server-time Online/Offline, validation.
- [x] Индивидуальные agent tokens, operator API policy, UI login/logout, TLS и CSRF.
- [x] Windows collector и явно включаемый macOS simulator; timeout/backoff.
- [x] Blazor список и автообновление через Interactive Server / SignalR.
- [x] PostgreSQL EF store, начальная миграция; отдельный dev/test InMemory store.
- [x] xUnit unit и HTTPS integration-тесты, скрипт локальной настройки.
- [x] Независимое read-only архитектурное review, исправления валидации uptime и TLS intermediates.
- [ ] Git init — заблокирован запретом записи в `.git`, выполнить в обычном терминале.
- [ ] Полная сборка Blazor assets и стандартный test runner в обычной среде.
- [ ] End-to-end browser/HTTPS и реальная БД/Windows — до приёмки Monitoring Core.

## Стабилизация v0.1: SignalR cookie challenge

- [x] Исправлено ошибочное ожидание 302 для `POST /_blazor/negotiate`: правильный статус без operator cookie — 401 по поведению ASP.NET Core 10.
- [x] Уточнён API-контракт: страница `/` возвращает 302 на вход, служебный SignalR endpoint — 401.
- [x] Добавлены регрессии настоящего CookieAuthenticationHandler на маршрутах приложения: `/` → 302, `/_blazor/negotiate` → 401; после operator login HTTPS negotiation возвращает 200 JSON.
- [x] Полная сборка solution без отключения Blazor assets: 0 ошибок, 0 предупреждений.
- [x] Полный прямой прогон xUnit: 25 тестов, 22 passed, 3 failed при подготовке HTTPS (`AppleCrypto.CreateTemporaryKeychain`), 0 skipped. Обе новые регрессии cookie challenge прошли.
- [ ] Подтвердить 25/25 в обычном терминале. Здесь стандартный `dotnet test` прерван до тестов: VSTest socket bind получает `SocketException (13): Permission denied`; HTTPS fixture отдельно упирается в AppleCrypto, до HTTP-запросов.

## Следующие независимые задачи

| Приоритет | Задача / агент | Область владения | Критерий готовности |
|---|---|---|---|
| P0 | Windows verification | Agent/Telemetry, отдельный отчёт | CPU сопоставлен с Task Manager, RAM/диски/uptime проверены; SCM stop/start/recovery и перезагрузка |
| P0 | PostgreSQL verification | Новые integration-тесты, отдельный test DB | Применение/повтор миграций, concurrent upsert, restart persistence, disconnect/readiness, backup/restore |
| P0 | Security review, read-only | Отчёт без изменения общих файлов | Bearer/cookie границы, CSRF, hub auth, TLS, secrets/ACL, ограничение запросов |
| P1 | UI verification/accessibility | Components, wwwroot | Реальный browser E2E: login, online/offline, reconnect, stale/error состояния, mobile и клавиатура |
| P1 | Нагрузочный/soak тест | scripts/load + отчёт | 100 UUID, 10 s interval, 24 h, latency/error rate/CPU/RAM/БД, reconnect storm |
| P1 | Deployment/operations | docs/deployment, packaging | TLS renewal, config ACL, Windows service recovery, серверный service, keyring, runbook |

Contracts, `Program.cs`, csproj/solution, общие конфиги и миграции изменяет один интегратор.
Агенты согласуют предложения заранее; никакого параллельного редактирования этих файлов.
Для БД тестов сначала использовать существующий schema; изменения миграций — отдельный последовательный шаг.
Любое продуктовое изменение после review проходит общую сборку и тесты.

## После приёмки Monitoring Core

1. Хранение истории с retention, индексы/агрегации и operational metrics.
2. Provisioning/revoke/rotate tokens, operator accounts/RBAC и аудит.
3. Процессы Unity/Resolume/vvvv: discovery по allowlist, PID/session, статус и restart detection.
4. Температуры: выбор доверенного источника, thresholds/hysteresis и модель unavailable.
5. Прогноз дисков на истории, оценка confidence и отсутствие данных.
6. Разрешённые приложения: allowlist IDs, ограниченные параметры, права, аудит и идемпотентные команды.
7. AI-анализ логов: только после стабильного Monitoring Core; предварительная очистка чувствительных данных.
