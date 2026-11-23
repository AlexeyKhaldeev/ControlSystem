# Настройка агентов Codex

Проверено 23 ноября 2026, Codex CLI `0.171.0-alpha.13`, .NET SDK `10.0.100`.
Ветка `chore/codex-agents`; исходный commit `68727ad` совпадает с локальной `main`.
Код приложения, тесты, solution и API-контракты не изменялись. Commit/push/merge/deployment не выполнялись.

## Конфигурация и использование

`.codex/config.toml` содержит `[agents]`, `enabled = true` и
`max_concurrent_threads_per_session = 3`. Лимит относится к дочерним потокам,
основной не учитывается. Пять ролей автоматически обнаруживаются из `.codex/agents/`:

| Роль | Ответственность |
|---|---|
| architect | Архитектура, API-контракты, зависимости; по умолчанию read-only |
| backend | ASP.NET Core, Security/Monitoring/Data, EF Core/PostgreSQL, серверные тесты |
| windows_agent | Worker, Telemetry/Transport, heartbeat, Windows-тесты |
| frontend | Components/wwwroot, Dashboard, UX, ошибки и доступность |
| reviewer | Независимый read-only аудит качества, security, регрессий и покрытия |

Каждый файл содержит `name`, `description`, `developer_instructions`.
Настройки модели/reasoning отсутствуют: наследуются от основного агента.
Reviewer не пишет файлы и возвращает отчёт сообщением. Его инструкции сами по себе
не создают отдельную sandbox: права наследуются. Проверочный запуск выполняется
из родительской сессии с `--sandbox read-only`.

Пример запроса координатору:

> Запусти architect и reviewer с соответствующими custom roles для независимых
> read-only ревью, дождись обоих и объедини выводы. Не изменяй файлы.

Независимые задачи с изменениями выполняются в отдельных worktrees, после назначения
владельцев файлов. Contracts, Program.cs, solution/csproj, миграции и общие настройки
интегрирует последовательно главный агент. Настройки проекта следует использовать в
доверенном checkout; после изменения ролей проверять их доступность в новой сессии.

## Проверки

- Python `tomllib`: все 6 TOML синтаксически корректны; проверены обязательные поля,
  пять уникальных имён, точные параметры `[agents]`, отсутствие фиксации моделей.
- `codex debug prompt-input`: все 5 ролей присутствуют в `Available roles`.
- Запуск `codex exec --strict-config` принимает конфигурацию без неизвестных полей.
- `dotnet build ControlSystem.sln`: успешно, 0 ошибок, 0 предупреждений.
- `dotnet test ControlSystem.sln`: успешно, 25 passed, 0 failed, 0 skipped.
- Build/test потребовали разрешённого запуска вне sandbox: внутри MSBuild не мог
  создать IPC/socket (`SocketException (13): Permission denied`).
- Первая попытка запуска subagents с `--ephemeral` не создала дочерних потоков:
  `invalid thread-store request: no rollout found for thread id`. Для фактической
  проверки выполнен повторный запуск с сохранением сессии и read-only sandbox.

## Источники

Формат сверён с актуальным официальным руководством, загруженным 23 ноября 2026:
[Subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents.md),
[Configuration Reference](https://learn.chatgpt.com/docs/config-file/config-reference),
[Worktrees](https://developers.openai.com/codex/app/worktrees).
Custom roles не переопределяют права родителя; модель/reasoning наследуются при
отсутствии явных настроек. Настройки не требуют legacy role tables или model pinning.


## Фактический запуск и объединённый аудит

Родительская read-only сессия: `01a20933-a1fa-75b0-84f2-42e2fccce5e5`.
Оба агента завершились; применены специализированные developer_instructions.
Имена ролей подтверждены не только сообщениями агентов, но и `session_meta`:

| Задача | Custom role | Дочерний thread UUID |
|---|---|---|
| `/root/architecture_audit` | architect | `01a20933-cd02-7d01-be06-6fa9a97be4f8` |
| `/root/security_review` | reviewer | `01a20933-e4fa-79c1-ac2a-519ce8220d99` |

`source.subagent.thread_spawn` указывает родителя, depth=1 и соответствующую
agent_role. Инструмент запуска вернул task paths; UUID отдельно сверены с metadata.
Вложенных агентов не создавали. Аудиторы не меняли файлы и не запускали build/test;
общие проверки выполнил координатор. Backend/windows_agent/frontend обнаружены,
но в рамках этой задачи не запускались.

Архитектура соответствует заявленным границам: Server и Agent зависят только от
Contracts; Windows API изолирован в Telemetry; DbContext создаётся через factory
на операцию; PostgreSQL upsert параметризован; LastSeenAtUtc задаёт сервер,
статус вычисляется при чтении. InMemory не является fallback.

| Приоритет | Вывод | Доказательство / следующий шаг |
|---|---|---|
| P1 | Возможное сохранение доступа открытого Blazor circuit после истечения cookie | `src/ControlSystem.Server/Program.cs:74`, `:85`, `:134`, `Components/Pages/Home.razor:79`. Перепроверка auth/закрытие по expiry не найдены; статический риск, не воспроизведён. Нужен browser E2E и решение о завершении сессии. |
| P1 | Недостаточное покрытие security-регрессий | `tests/ControlSystem.Tests/ApiTests.cs:25`, `AuthenticationTests.cs:42`: добавить logout CSRF/session end, bearer-only SignalR, 429, 413, HTTP/query-token rejection. Пробелы тестов не доказывают обход защиты. |
| P1 | TLS negative coverage неполно | `ApiTests.cs:74` проверяет системное недоверие без TrustedCaPath, а не другую явно заданную CA. Добавить wrong custom CA, hostname mismatch, expiry и intermediates. Обход hostname validation в коде не найден. |
| P2 | Порог Offline в документации 35 s, в конфигурации разрешено 20–300 s | `docs/api.md:71`, `requirements.md:18`, `Program.cs:48`, `Monitoring/MonitoringService.cs:24`. Уточнить семантику отдельной задачей, сейчас контракт не изменён. |
| P2 | Пауза backoff сокращается на длительность попытки | `src/ControlSystem.Agent/TelemetryWorker.cs:22`, `:38`–`:40`. Уточнить требование отсчёта от начала/конца попытки и добавить scheduling/cancellation tests. |
| P2 | Ссылка на отсутствующий verification.md и устаревшие исторические статусы | `docs/tasks.md:3`, `:26`. Новый раздел tasks и этот отчёт фиксируют актуальные 25/25; прежние результаты не удалены. |

Вывод аудиторов о необходимости успешного build/test закрыт проверками координатора:
0 ошибок/предупреждений, 25/25 tests. Это не закрывает приёмку на реальных Windows
и PostgreSQL, browser E2E, нагрузку 100 агентов и эксплуатационные проверки.
Тест concurrency сейчас использует InMemory; EF-тест генерирует модель/SQL офлайн.
Секреты, production ACL, keyring и реальные deployment-настройки не проверялись.

## Предложение v0.2 (разработка не начата)

Сначала отдельной задачей завершить приёмку Monitoring Core:

1. Backend: PostgreSQL integration — migrations/re-run, concurrent upsert, persistence,
   readiness при disconnect и backup/restore; reviewer проверяет результаты.
2. Windows_agent: реальные CPU/RAM/disks/uptime, SCM stop/start/recovery/reboot;
   симулятор не заменяет Windows host.
3. Frontend + reviewer: browser HTTPS/login/logout/SignalR/reconnect/session expiry,
   stale/error, keyboard/mobile; расширить security/TLS regression coverage через
   назначенного владельца тестовых файлов.
4. Координатор: load/soak 100 UUID с heartbeat 10 s, reconnect storm, TLS renewal,
   config ACL и устойчивый защищённый DataProtection keyring. Зафиксировать критерии
   latency/error rate/resources до прогона. Принять Monitoring Core явно.

Затем согласовать ограниченный scope v0.2 и ADR с architect:

1. История метрик и retention: отдельная схема/миграции, индексы и PostgreSQL tests;
   backend реализует после последовательного согласования Contracts/модели координатором.
2. Provisioning/revoke/rotate credentials, operator accounts/RBAC и аудит — отдельный
   security этап с reviewer; UI реализует frontend по утверждённым контрактам.
3. Наблюдение allowlist процессов Unity/Resolume/vvvv — только после утверждения scope,
   отдельные Windows acceptance tests. Не объединять с произвольным remote execution.

Команды для разрешённых приложений требуют отдельного контракта, ролей, аудита и
идемпотентности; AI остаётся последующим этапом. Независимые изменения — worktrees,
один владелец на файл, максимум 3 дочерних потока, общие build/test после интеграции,
без автоматического merge в main и без коммитов без разрешения.
