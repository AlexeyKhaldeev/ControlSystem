# GitHub Actions CI

Workflow: [ci.yml](../.github/workflows/ci.yml), репозиторий
`AlexeyKhaldeev/ControlSystem`. Запуски: pull request с target `main` и push в `main`.
Два независимых job на `ubuntu-24.04`, SDK `10.0.x` с учётом `global.json`:

- `Build and test (PostgreSQL 17)`: `dotnet restore ControlSystem.sln`,
  `dotnet build ControlSystem.sln`, `dotnet test ControlSystem.sln`.
- `Publish Agent (win-x64 self-contained)`:
  `dotnet publish src/ControlSystem.Agent/ControlSystem.Agent.csproj -c Release -r win-x64 --self-contained true`.
  Явный путь нужен, поскольку проекта `ControlSystem.Agent` в корне нет.
  Publish выполняет собственный restore для Windows RID и WindowsServices package.

PostgreSQL 17 запускается через `services`, с health check и отдельной временной
базой `controlsystem_ci_test`. Job передаёт `CONTROLSYSTEM_TEST_POSTGRES` всем шагам;
все 8 PostgreSQL cases включаются существующим `SkipUnless`. Нет test filters,
`continue-on-error`, подмены хранилища или изменений ожиданий тестов. Неработающая
БД приводит к ошибке, а не к fallback. Тесты создают случайные схемы и применяют
миграции, поэтому CI-пользователь владеет исключительно тестовой БД.

Credentials в YAML относятся только к уничтожаемому service container. GitHub
Secrets, production БД, `.local`, операторские и агентские credentials не нужны.
`GITHUB_TOKEN` имеет только `contents: read`; checkout не сохраняет credentials.
Используется `pull_request`, без `pull_request_target` и привилегированных действий.
Workflow ничего не публикует во внешнюю среду и не выполняет deployment.
HTTPS, hostname validation и авторизация приложения сохраняются.

Устройство services соответствует [документации GitHub](https://docs.github.com/en/actions/tutorials/use-containerized-services/create-postgresql-service-containers);
SDK устанавливается через [setup-dotnet](https://github.com/actions/setup-dotnet).

## Локальное воспроизведение

Используйте .NET SDK 10 и отдельный PostgreSQL 17. Передайте connection string
через `CONTROLSYSTEM_TEST_POSTGRES` (не production), затем выполните четыре команды
выше. Без этой переменной существующие PostgreSQL cases будут skipped — такой
прогон не подтверждает полную CI-проверку. Connection string и пароли не коммитить.

## План Browser E2E

Browser project остаётся вне solution и обязательного CI; текущие сценарии и
Mac-инструкция: [browser-e2e.md](browser-e2e.md). Следующий отдельный инкремент:

1. Создать opt-in workflow с `workflow_dispatch`, отдельным job и явной установкой
   совместимого Google Chrome (`Channel=chrome`) и зависимостей Playwright.
2. На disposable runner создать краткоживущий тестовый CA и leaf с SAN `localhost`
   (и IP SAN `127.0.0.1`, если он используется), serverAuth и отдельным private key.
   Установить CA в системное и фактически используемое Chrome хранилище доверия;
   проверить выбранную версию Chrome на этом runner.
3. Передать `CONTROLSYSTEM_E2E_CERT`, `CONTROLSYSTEM_E2E_KEY`,
   `CONTROLSYSTEM_E2E_CA` и `NODE_EXTRA_CA_CERTS` для Node/APIRequest. Ограничить права
   private key; не включать ключи, cookies, tokens или raw frames в артефакты.
4. Запустить все 9 cases отдельным `dotnet test` для BrowserTests: успешные HTTPS
   login/live updates/reconnect и обязательный отрицательный контроль недоверенного
   сертификата. Добавить отрицательный контроль hostname mismatch до продвижения
   job в обязательные проверки. Не использовать IgnoreHTTPSErrors,
   `--ignore-certificate-errors`, `NODE_TLS_REJECT_UNAUTHORIZED=0` или skip prerequisites.
5. Проверить воспроизводимость нескольких чистых запусков; очистку trust/key files
   выполнять в `always()`; после отдельного review согласовать required status.

## Оставшиеся риски

- Первый GitHub-hosted запуск ещё нужно подтвердить после push/PR; локальный Mac
  не воспроизводит runner/services полностью. Изменения не отправляются автоматически.
- Branch protection/required status checks настраиваются отдельно в GitHub после
  появления обоих job. Наличие YAML само по себе не блокирует merge.
- Cross-publish не проверяет Windows SCM, реальные метрики, ACL и EventLog.
- Browser TLS, длительная нагрузка, backup/restore и crash recovery вне этого CI.
- Action major tags и `postgres:17` обновляемы; SHA/digest pinning и автоматическое
  обновление зависимостей можно ввести отдельной задачей.

## Проверки 27 ноября 2026

Локально macOS arm64, SDK 10.0.100, отдельный Docker PostgreSQL 17 на loopback:55437.
Команды .NET выполнены вне sandbox: ограниченные запуски restore/build зависли
без вывода; Docker socket внутри sandbox недоступен.

- `dotnet restore ControlSystem.sln`: exit 0.
- `dotnet build ControlSystem.sln`: exit 0, 0 warnings / 0 errors.
- `dotnet test ControlSystem.sln` с `CONTROLSYSTEM_TEST_POSTGRES`: exit 0,
  65 passed / 0 failed / 0 skipped, 4 s, включая все 8 PostgreSQL cases.
- Self-contained `win-x64` publish указанной выше командой: exit 0.
- YAML успешно разобран Ruby/Psych, обнаружены оба job; `git diff --check`: exit 0.
  Это проверка синтаксиса YAML, не выполнение GitHub Actions и не actionlint.
- Независимый read-only reviewer audit: подтверждённых дефектов и блокирующих
  замечаний нет. Риски: первый hosted run, required checks, обновляемые tags,
  отсутствие нативной Windows-приёмки.

Продуктовый код, тесты и ожидания не изменялись. Browser E2E не запускался.
Commit, push, merge и deployment не выполнялись.
