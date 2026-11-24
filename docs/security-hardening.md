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
- Commit, merge, push и deployment не выполнялись.

Основание подхода: [Microsoft: Blazor authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/blazor/security/?view=aspnetcore-10.0).

Неблокирующий риск тестов: короткий двухсекундный срок cookie в regression test может
дать нестабильность на перегруженном CI; при проявлении следует заменить wall-clock ожидание
управляемым TimeProvider для cookie, сохранив проверку реального handler.
