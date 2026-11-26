# Security Hardening — UI-cookie и Blazor circuit

Дата: 23 ноября 2026. Worktree `security-circuit`, ветка `codex/security-circuit`, база `main`.
Специализированные агенты: backend (реализация Security и регрессии), reviewer (независимый read-only аудит).

## Подтверждение исходных дефектов

До изменения продуктового кода запущено:

```sh
dotnet test ControlSystem.sln --filter FullyQualifiedName~CircuitSecurityTests
```

Результат: 2 failed, 0 passed, 0 skipped.

1. `Circuit_authentication_is_invalidated_after_real_cookie_expires`: выдаётся настоящая
   cookie через CookieAuthenticationHandler с коротким expiry; principal передаётся в
   зарегистрированный приложением AuthenticationStateProvider так, как это делает host.
   После expiry новый AuthenticateAsync уже отклоняет cookie, но provider продолжает
   возвращать authenticated principal (включая ещё 3 секунды ожидания).
2. `Logout_revokes_replayed_cookie_and_existing_circuit_authentication`: после реального
   SignOutAsync handler выдаёт удаляющую Set-Cookie, но повторный AuthenticateAsync с
   сохранённой cookie принимает principal. Проверка падает на replay до проверки provider.

Это воспроизведение на реальных компонентах аутентификации приложения, без HTTP-сокетов
и живого circuit. Оно не доказывает доставку render diff браузеру через WebSocket.
Код Home ранее читал MonitoringService каждые 5 секунд без повторной проверки доступа;
проверка маршрута сама по себе не защищала последующие чтения.

## Исправление

- Серверный volatile registry хранит случайный 256-bit session ID и expiry каждой UI-сессии.
  Cookie events привязывают ID к подписанному ticket, проверяют сессию при запросах и
  отзывают её при logout. Сохранённая cookie после logout больше не принимается.
- Circuit provider проверяет сессию раз в секунду и переводит authentication state в anonymous.
- AuthorizeView находится внутри интерактивного Home. Проверки сессии до чтения и
  после await не дают публиковать snapshot, прочитанный при уже отозванной сессии.
- Срок cookie остаётся 8 часов без sliding expiration; TLS, antiforgery и bearer policies
  не ослаблены. Contracts, миграции, конфигурация агентов и AGENTS.md не изменены.

## Проверки и независимое review

Исходная и исправленная solution собираются с 0 warnings / 0 errors.
Регрессии проверяют expiry, logout/replay, fail-closed session validation, точную границу
expiry, независимость сессий, отрисовку Home и отзыв во время чтения хранилища.
Финальные проверки после интеграции:

```sh
dotnet build ControlSystem.sln
dotnet test ControlSystem.sln
```

Build: exit 0, 0 warnings, 0 errors. Tests: exit 0, 32 passed, 0 failed,
0 skipped (включая 7 новых regression cases), длительность тестов 4 секунды.
Проверки выполнены вне sandbox после разрешения: локальные HTTPS-тесты также прошли.
`git diff --check` — exit 0.
Reviewer не нашёл блокирующих проблем в исправлении. Его замечание об изоляции
проверок Home от периодической revalidation учтено в тестовом harness.

## Ограничения и следующие проверки

- Нужен browser/WebSocket E2E: два окна с одной cookie, отдельный независимый вход,
  обновляющиеся heartbeat, logout/expiry, попытка reconnect старого circuit.
- Static HTML harness проверяет Home и данные, но не запускает OnAfterRender и timer.
- Состояние circuit обновляется на следующем тике проверки (обычно около секунды,
  возможны задержки планировщика). Физическое закрытие WebSocket не гарантируется.
- Уже доставленные клиенту данные не могут быть отозваны. Проверки не являются
  транзакционной синхронизацией с записью в сеть: отзыв после последней проверки
  не может отменить уже отправляемый ответ.
- Перезапуск сервера завершает все UI-сессии, включая cookies до обновления.
  Registry рассчитан на единственный deployable сервер; административного revoke API
  и отзыва всех сессий при ротации operator token в этой задаче нет.
- На этапе исходного hardening commit, merge, push и deployment не выполнялись; последующая интеграция описана ниже.

Основание подхода: [Microsoft: Blazor authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0).

Неблокирующий риск тестов: короткий двухсекундный срок cookie в regression test может
дать нестабильность на перегруженном CI; при проявлении следует заменить wall-clock ожидание
управляемым TimeProvider для cookie, сохранив проверку реального handler.


## Интеграция Dashboard — 24 ноября 2026

- Создан коммит `308efa5` — `fix: harden operator sessions and Blazor circuits`.
- Выполнен `git fetch origin`; в `codex/security-circuit` интегрирована `origin/main`
  на `2e8d6c3` с уже объединёнными Dashboard Improvements.
- Единственный текстовый конфликт — `docs/tasks.md`: обе ветки добавили раздел перед
  списком следующих задач. Сохранены оба раздела, их результаты и открытые E2E-проверки.
- `Home.razor` объединился автоматически и проверен вручную: сохранены фактическая версия,
  подпись RAM, пустые диски, Offline snapshot, различение ошибки и пустого списка,
  AuthorizeView и проверки UI-сессии до/после чтения. Выбор ours/theirs не применялся.
- В `DashboardTests.cs` адаптирован только DI harness: действующая тестовая UI-сессия,
  provider состояния, authorization и cascading state. Все assertions Dashboard сохранены.
- Проверен состав изменений относительно `origin/main`: 11 относящихся к задаче файлов,
  без изменений Contracts/Agent, конфигураций, credentials или сертификатов.
  Поиск характерных private-key/password/GitHub-token маркеров в новых исходниках и
  тестах ничего не нашёл; литералы токенов — заведомо синтетические тестовые значения.
- `dotnet build ControlSystem.sln`: exit 0, 0 warnings, 0 errors.
- `dotnet test ControlSystem.sln`: exit 0, **37 passed, 0 failed, 0 skipped**, 4 секунды.
  Включены все 7 Security и 5 Dashboard regression cases.
- Команды выполнялись вне sandbox после разрешения; sandbox build вновь завис без вывода.
- Независимый read-only reviewer проверил объединённый код и тестовый harness: блокирующих замечаний нет; Dashboard assertions, security guards и границы компонентов сохранены.
- Browser/WebSocket E2E остаётся открытой проверкой. Push и merge в main не выполнялись.


## Browser E2E Validation — 24 ноября 2026

Ветка, подготовленная пользователем: `test/browser-e2e`. Product-код, Contracts и Agent
не изменялись. Участвовали frontend (сценарии, тесты и анализ DI) и reviewer
(независимое read-only ревью реализации и документации).

Полный запуск в настоящем Google Chrome через Microsoft.Playwright 1.65.0,
Kestrel HTTPS и доверенный localhost-сертификат. TLS validation не отключалась;
отдельный отрицательный case подтвердил `ERR_CERT_AUTHORITY_INVALID` для недоверенного
self-signed сертификата. С разрешения установлен .NET localhost dev certificate
в доверие macOS; PEM/key экспортированы в игнорируемую `.local/browser-e2e`, key mode 600.
Шаги запуска и отзыва доверия: [browser-e2e.md](browser-e2e.md).

Проверены login/anonymous, Secure/HttpOnly/Strict cookie, bearer-only API boundary,
logout открытого Dashboard в двух вкладках, cookie replay, expiry, независимость сессий,
реальный refresh, восстановление того же circuit без reload, offline → logout/expiry
→ отказ повторной авторизации. После завершения сессии новые telemetry markers
не приходят в DOM/входящих WebSocket frames; наблюдатель frames имеет положительный
контроль. `ListAsync` не вызывается в шестисекундном окне, пересекающем реальный
пятисекундный refresh tick.

Результаты финального прогона (вне sandbox):

| Команда | Результат |
|---|---|
| `dotnet build ControlSystem.sln` | exit 0; 0 warnings, 0 errors |
| `dotnet test ControlSystem.sln` | exit 0; 37 passed, 0 failed, 0 skipped; 4 s |
| `./scripts/browser-e2e.sh --no-restore --logger 'console;verbosity=normal'` | exit 0; 9 passed, 0 failed, 0 skipped; 95 s |
| `git diff --check` | exit 0 |

Всего **46 passed**. E2E — отдельный opt-in проект, не включённый в solution;
без Chrome/сертификата тесты не подменяются skip.
Reviewer не обнаружил блокирующих дефектов в конечной реализации.

Подтверждённых продуктовых дефектов или новых утечек в проверенных сценариях не найдено.
Во время разработки harness первый E2E-прогон дал 1 passed / 6 failed: ранние DI
Replace были перекрыты регистрациями Program, и тест сеял snapshot в другой store.
Исправлено test-only IServiceProviderFactory с поздней заменой store/clock и fail-fast
проверкой ReferenceEquals. Затем исходные 7 cases прошли; финальный расширенный набор
из 9 cases тоже прошёл. Продуктовый код для получения green не изменялся.
Sandbox restore первоначально завершился NU1301/DNS, sandbox build завис без вывода;
проверки выполнены вне sandbox после разрешения.

Оставшиеся границы: ранее полученные bytes/DOM невозможно отозвать у отключённого
браузера; отзыв не транзакционен с уже отправляемым ответом. Проверен один Chrome/macOS,
синтетический snapshot публикуется напрямую в store (HTTPS heartbeat отдельно покрыт
существующими ApiTests). Не проверены packet loss/blackhole, длительный outage/soak,
reconnect storm, timer disposal после retention timeout, другие браузеры,
PostgreSQL и Windows. Expiry использует сдвиг test-only серверных часов, не восьмичасовое
ожидание. Следующие проверки — отдельный network/soak и cross-browser прогон.
Commit, push, merge и deployment в этой задаче не выполнялись.
