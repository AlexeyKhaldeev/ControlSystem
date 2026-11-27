# ControlSystem

LAN-мониторинг до 100 Windows-компьютеров интерактивной экспозиции. Стартовый
Monitoring Core: heartbeat по HTTPS, CPU/RAM/диски/uptime, Online/Offline и Blazor-панель.
Это прототип; готовность к промышленной эксплуатации ещё требует Windows, PostgreSQL и нагрузочной проверки.

```text
ControlSystem.sln
src/
  ControlSystem.Server/       API, Security, Monitoring, EF Data, Blazor Components
  ControlSystem.Agent/        Worker, Windows/Simulated Telemetry, HTTPS Transport
  ControlSystem.Contracts/    DTO, версия протокола, validation
tests/
  ControlSystem.Tests/        xUnit v3: unit и HTTPS integration
docs/                        requirements, architecture, api, tasks, verification
scripts/dev-setup.py          локальные secrets и TLS CA
compose.yaml                 PostgreSQL 17, порт только loopback
AGENTS.md                    правила работы агентов
```

## Запуск на macOS

Требуются .NET SDK 10 (global.json допускает установленные стабильные 10.0 feature bands),
Python 3 и OpenSSL для dev-настройки. При первом restore нужен доступ к NuGet.

Из корня проекта:

```bash
python3 scripts/dev-setup.py
source .local/dev.env
dotnet restore ControlSystem.sln
dotnet build ControlSystem.sln --no-restore
dotnet test ControlSystem.sln --no-build
dotnet run --no-launch-profile --project src/ControlSystem.Server
```

Во втором терминале, также из корня:

```bash
source .local/dev.env
dotnet run --no-launch-profile --project src/ControlSystem.Agent -- --simulate
```

Открыть [https://localhost:7443](https://localhost:7443), войти с ключом из
`cat .local/operator-token`. Браузер должен доверять локальному CA: на Mac импортировать
**публичный** `.local/ca.crt.pem` в Keychain Access и доверить ему SSL для локальной разработки.
Приватный CA key не импортировать и не передавать. Агент доверяет этому CA через
Agent__TrustedCaPath без изменения системного keychain; проверки hostname/TLS включены.

Скрипт создаёт уникальные secrets и сертификаты на 30 дней, не перезаписывает существующие.
`.local` исключён из Git; файлы с secrets доступны только текущему пользователю.
Для обновления dev-настройки сначала остановить процессы и переименовать/удалить `.local`,
затем повторить генерацию. Старые device UUID/credentials при этом перестанут действовать.

Тестовый сервер использует InMemory: после перезапуска список очищается. Симулятор
генерирует синтетические данные, имеет hostname `SIM-*` и метку в UI. Без `--simulate`
Agent на Mac/Linux завершается с понятной ошибкой. После остановки агента статус станет
Offline через >35 секунд; UI обновляется раз в 5 секунд.

Проверка API без отключения TLS:

```bash
curl --cacert .local/ca.crt.pem https://localhost:7443/health/ready
curl --cacert .local/ca.crt.pem \
  -H "Authorization: Bearer $(cat .local/operator-token)" \
  https://localhost:7443/api/v1/devices
```

## PostgreSQL на Mac

Запустить Docker Desktop. В терминале с загруженным dev.env:

```bash
export POSTGRES_PASSWORD='replace-with-a-long-local-password'
docker compose up -d db
export Database__Provider=PostgreSql
export ConnectionStrings__Monitoring="Host=localhost;Port=5432;Database=controlsystem;Username=controlsystem;Password=$POSTGRES_PASSWORD"
dotnet run --no-launch-profile --project src/ControlSystem.Server -- --Database:ApplyMigrations=true
dotnet run --no-launch-profile --project src/ControlSystem.Server
```

Это остаётся Development с dev-сертификатами; PostgreSQL данные хранятся в Docker volume.
Не выполнять `docker compose down -v`, если нужны данные. В production использовать отдельную
роль для миграций и runtime role без DDL; обычный сервер не применяет миграции автоматически.

## Windows Agent

Сборка из Mac или Windows (требует NuGet и runtime packs):

```bash
dotnet publish src/ControlSystem.Agent -c Release -r win-x64 --self-contained true -o artifacts/agent-win-x64
```

`win-*` автоматически включает Windows Service adapter. Альтернатива — явно
`-p:EnableWindowsService=true`. В framework-dependent варианте текущему Agent нужен
ASP.NET Core Runtime 10; self-contained publish включает runtime.

На каждом ПК задать `Agent__ServerUrl` (HTTPS root URL), стабильный `Agent__DeviceId`,
индивидуальный `Agent__Token`; опционально `Agent__TrustedCaPath` для LAN CA.
Сервер должен знать UUID и SHA256 именно этого token. Не раздавать один token всем ПК.
Для службы поместить Agent-секцию в защищённый `appsettings.json` рядом с exe или
задать service-specific environment; права на secrets только service account и администратору.

В повышенной PowerShell после размещения файлов:

```powershell
sc.exe create ControlSystem.Agent binPath= 'C:\ControlSystem\Agent\ControlSystem.Agent.exe' start= auto
sc.exe failure ControlSystem.Agent reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe start ControlSystem.Agent
```

Установку службы и native-метрики обязательно проверить на Windows. Симуляция этого не проверяет.

## Production server

Явно настроить Production environment, PostgreSQL connection string, TLS-сертификат
на реальное LAN-имя, AllowedHosts, индивидуальные hashes, ACL и защищённый persistent
DataProtection keyring. Сервер сейчас запускается напрямую через Kestrel; forwarded
headers/reverse proxy не настроены. InMemory и незашифрованный HTTP запрещены.
Ротация/отзыв credentials и полноценные operator accounts ещё в плане.

Документация: [требования](docs/requirements.md), [архитектура и ADR](docs/architecture.md),
[API и модель](docs/api.md), [план задач](docs/tasks.md), [результаты проверок](docs/verification.md).
Состояние текущей среды и точные ограничения сборки/тестов указаны в verification.md.

Git init в среде подготовки заблокирован правами на `.git`. В обычном терминале:

```bash
git init -b main
git status --short
```
