# План работ и делегирование

Обновлено: 24 ноября 2026. Проверки и ограничения среды — `verification.md`.

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

## Security Hardening: cookie и Blazor circuit

- [x] В отдельном worktree от main воспроизведены сохранение authenticated principal после expiry cookie и replay cookie после logout (настоящий handler + зарегистрированный Blazor provider).
- [x] Серверные UI-сессии: expiry, отзыв при logout, отказ при replay и перезапуске.
- [x] Повторная проверка circuit раз в секунду; Home проверяет доступ до/после чтения и скрывает защищённый UI.
- [x] Независимое read-only review специализированным reviewer; блокирующих замечаний по коду и регрессиям нет.
- [x] Итоговый dotnet build: 0 warnings/errors; dotnet test: 32 passed, 0 failed, 0 skipped (7 новых регрессий).
- [x] Browser/WebSocket E2E с двумя вкладками: обновление UI и reconnect после expiry/logout; см. `browser-e2e.md`.

- [x] Интеграция Dashboard из `origin/main` (`2e8d6c3`), 24 ноября 2026: оба раздела задач и все исправления Home сохранены; адаптирован DI harness DashboardTests. Итоговый build: 0 warnings/errors; tests: 37 passed, 0 failed, 0 skipped (7 Security + 5 Dashboard регрессий).

Подробности воспроизведения, регрессии, результаты и ограничения: `security-hardening.md`.

## Dashboard Improvements — 23 ноября 2026

- [x] Исправлено Razor-выражение версии агента: `v@(device.AgentVersion)` выводит фактическое значение.
- [x] Уточнена подпись RAM (занято / всего), пустые диски обозначены прочерком; Offline поясняет, что показан последний снимок.
- [x] При ошибке первого чтения не показывается ложное ожидание первого heartbeat.
- [x] Добавлены 5 регрессионных UI-тестов: версия, CPU/RAM/диски в en-US и ru-RU, uptime, прогрев CPU, Online/Offline на границе 35 секунд, ошибка чтения, повторный рендер и восстановление.
- [x] Frontend проверил цикл обновления и disposal; независимый read-only reviewer не обнаружил дефектов. API, DTO, авторизация и Windows Agent не изменены.
- [x] В worktree `dashboard-improvements`, ветка `codex/dashboard-improvements`: `dotnet build ControlSystem.sln` — 0 warnings / 0 errors; `dotnet test ControlSystem.sln` — 30 passed / 0 failed / 0 skipped (946 ms). Выполнено вне sandbox после зависания sandbox-запуска без вывода.
- [x] Реальный пятисекундный таймер и SignalR/reconnect проверены Browser E2E ниже. Статический renderer сам по себе не запускает OnAfterRenderAsync и timer.
- [ ] Отдельно проверить отмену timer после закрытия circuit и истечения retention timeout; Browser E2E этой задачи проверяет прекращение чтений после logout/expiry.

## v0.2 — Browser E2E Validation, 24 ноября 2026

- [x] Изучены circuit/session guards и существующие авторизационные тесты; frontend помог с browser-сценариями и DI harness.
- [x] Добавлен отдельный opt-in Playwright/xUnit проект: настоящий Chrome + Kestrel HTTPS, runtime tokens, ephemeral DataProtection, явно выбранный Testing/InMemory и counting store.
- [x] Login, anonymous access, logout в двух вкладках, cookie replay, expiry, независимые сессии, live refresh и reconnect без reload с сохранением circuit.
- [x] Offline → logout/expiry → reconnect: отказ negotiation, запрет новых telemetry reads и отсутствие нового marker в DOM/входящих frames с положительным контролем observer.
- [x] Отрицательный browser TLS control с недоверенным self-signed сертификатом; TLS/hostname validation не отключаются.
- [x] Воспроизводимый Mac запуск: `scripts/browser-e2e.sh` и `docs/browser-e2e.md`; E2E запускается отдельно от solution и не скрывает отсутствие prerequisites через skip.
- [x] Финальные проверки вне sandbox: `dotnet build ControlSystem.sln` — exit 0, 0 warnings/errors; `dotnet test ControlSystem.sln` — 37 passed / 0 failed / 0 skipped, 4 s; Browser E2E — 9 passed / 0 failed / 0 skipped, 95 s. Всего 46 passed. Независимое read-only reviewer review: блокирующих замечаний нет; `git diff --check` — exit 0.
- Ограничения: ранее доставленный offline DOM не отзывается; packet loss/blackhole, reconnect storm, длительный soak и disposal после circuit retention timeout не проверены. PostgreSQL/Windows вне этого прогона.
- Product/Contracts/Agent не изменены. Commit, push, merge, deployment не выполнялись.

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
