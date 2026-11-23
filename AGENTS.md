# ControlSystem — инструкции для разработки

## Цель и стек

LAN Monitoring Core: один сервер, до 100 Windows ПК, heartbeat каждые 10 секунд.
C# / .NET 10 LTS, ASP.NET Core API, Blazor Interactive Server / SignalR,
PostgreSQL / EF Core, Windows Worker Service, xUnit v3.

## Границы

- `src/ControlSystem.Contracts`: только DTO, версия протокола и проверка данных; без ASP.NET, EF и Windows API.
- `src/ControlSystem.Server`: API, безопасность, application services, хранилище, Blazor UI. Зависит только от Contracts.
- `src/ControlSystem.Agent`: сбор метрик и HTTPS transport; зависит только от Contracts. Windows API изолирован в `Telemetry`.
- `tests/ControlSystem.Tests`: unit и интеграционные HTTPS-тесты. Продуктовые проекты не зависят от Tests.
- Один deployable сервер. Не добавлять микросервисы, message broker, generic repository, CQRS framework без задачи и ADR.
- SQL только параметризованный. DbContext короткоживущий, через factory; не хранить его в Blazor circuit/singleton.
- Не реализовывать произвольные команды, shell, загрузку исполняемых файлов, AI до завершения Monitoring Core.
- Будущие команды — только allowlist приложений, роли, аудит, идемпотентность и отдельный контракт.

## Безопасность и данные

- HTTPS обязателен. Не отключать проверку сертификатов и hostname.
- У каждого DeviceId свой секрет; сервер хранит только SHA256 случайного токена, оператор имеет отдельный токен.
- Не коммитить `.local`, credentials, сертификаты с приватным ключом, connection strings с паролями.
- LastSeenAtUtc задаёт сервер. Online/Offline вычисляются при чтении, не хранятся в БД.
- Дата UTC, объёмы в bytes, CPU в процентах, uptime в seconds. Первый CPU sample может быть null.
- Нельзя подменять PostgreSQL на InMemory при ошибке. InMemory доступен только явно в Development/Testing и теряет данные при перезапуске.
- Контракты документировать в `docs/api.md`; изменения модели сопровождать EF migration и тестом snapshot.

## Рабочий процесс

1. Прочитать `docs/requirements.md`, `docs/architecture.md`, `docs/tasks.md` и существующий код.
2. Изменять только относящиеся к задаче файлы, не удалять чужие изменения.
3. Для существенной логики добавить тесты; `dotnet build ControlSystem.sln`, `dotnet test ControlSystem.sln`.
4. Обновить документацию и tasks; сообщить ограничения и точные результаты проверок.
5. Не делать commit, push или deployment без явного разрешения пользователя.

## Субагенты

Read-only архитектурные и security review разрешены. Изменения независимых компонентов
можно делегировать после назначения конкретных задач пользователем/ведущим агентом.
Один владелец на файл. Contracts, solution, миграции, глобальные настройки и Program.cs
редактируются последовательно ведущим агентом; остальные агенты предлагают изменения сообщением.
Перед интеграцией согласовать контракт и выполнить общие тесты.
